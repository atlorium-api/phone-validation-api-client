"""
Клиент API валидации телефона Atlorium — разбор номера любой страны.

Запуск (работает сразу, без регистрации — на демо-ключе):
    pip install -r requirements.txt
    python main.py
    python main.py "+79161234567,8 495 785-63-00"

Боевой ключ: получить на https://atlorium.com и положить в переменную окружения
ATLORIUM_API_KEY. Код при этом не меняется.

ГРАНИЦА СЕРВИСА. Разбор идёт по встроенному справочнику мировой нумерации, локально,
без обращения в сеть оператора. Сервис отвечает на вопрос «может ли такой номер
существовать и как он правильно записывается», но НЕ проверяет, пользуется ли номером
живой абонент. «Валидный» не значит «рабочий».
"""

import os
import sys
from dataclasses import dataclass, field
from urllib.parse import quote

import requests

# Публичный демо-ключ. С ним API отвечает правдоподобными МОКАМИ (не реальными
# данными) — чтобы можно было встроить и протестировать интеграцию до оплаты.
# Ответы детерминированы: один и тот же запрос всегда даёт один и тот же результат,
# поэтому на них можно писать стабильные тесты.
SANDBOX_KEY = "ak_sandbox_demo_mockdata_v1"

API_KEY = os.environ.get("ATLORIUM_API_KEY", SANDBOX_KEY)
BASE_URL = os.environ.get("ATLORIUM_BASE_URL", "https://atlorium.com")

TIMEOUT = 30

# Номера в разнобойных форматах — так и выглядит выгрузка из реальной CRM.
# Среди них есть один и тот же номер, записанный тремя способами, и одна строка-мусор.
DEMO_NUMBERS = [
    "+79161234567",
    "+7 (916) 123-45-67",
    "8 916 123-45-67",
    "+7 495 785-63-00",
    "+7 809 123-45-67",
    "+1 650 253 0000",
    "12345",
]


class AtloriumError(RuntimeError):
    """Ошибка API. Код HTTP разложен в человекочитаемую причину."""

    REASONS = {
        400: "Строка не является телефонным номером (или не указана страна для номера без «+»)",
        401: "API-ключ отсутствует, просрочен или недействителен",
        402: "Недостаточно кредитов на балансе — пополните на https://atlorium.com",
        429: "Превышен лимит запросов — повторите позже",
        503: "Сервис временно недоступен (за сбой на своей стороне мы не списываем деньги)",
    }

    def __init__(self, status: int, body: str):
        reason = self.REASONS.get(status, "Неизвестная ошибка")
        super().__init__(f"HTTP {status}: {reason}")
        self.status = status
        self.body = body


def lookup_phone(number: str, country_code: str | None = None) -> dict:
    """Карточка номера: валидность, форматы записи, страна, тип, оператор диапазона, часовые пояса.

    number — в международном формате ("+79161234567") или в национальном ("8 916 123-45-67").
    Пробелы, скобки и дефисы допустимы.

    country_code — ISO-3166 alpha-2 ("RU"): в какой стране трактовать номер, записанный БЕЗ «+».
    Для номеров с «+» не нужен и игнорируется.
    """
    # ВАЖНО: номер идёт в ПУТИ, поэтому кодируем его целиком (safe="").
    # «+» обязан превратиться в %2B — иначе часть веб-серверов и прокси прочитает его
    # как пробел, номер потеряет код страны и разбор развалится.
    path = f"/api/Phone/{quote(number, safe='')}"

    response = requests.get(
        f"{BASE_URL}{path}",
        params={"countryCode": country_code} if country_code else None,
        headers={
            "Authorization": f"Bearer {API_KEY}",
            "Accept": "application/json",
        },
        timeout=TIMEOUT,
    )
    if not response.ok:
        raise AtloriumError(response.status_code, response.text)
    return response.json()


# ── Применение данных: подготовка базы контактов к SMS-рассылке ───────────────
# Карточка номера сама по себе — просто JSON. Ценность появляется, когда по ней
# принимают решение. Ниже — то, что реально делают перед рассылкой: отсев номеров,
# на которые SMS не дойдёт или дойдёт дорого, приведение к E.164 и дедупликация.

# Решения по номеру.
KEEP = "KEEP"  # в рассылку
FLAG = "FLAG"  # в рассылку, но с пометкой риска
DUP = "DUP"  # дубль: тот же номер уже есть в списке
VOICE = "VOICE"  # SMS не примет, но годится для голосового обзвона
DROP = "DROP"  # выбросить


@dataclass
class Contact:
    raw: str  # как номер был записан на входе
    e164: str | None = None  # канонический формат — единственный, который принимают SMS-шлюзы
    number_type: str | None = None
    country_code: str | None = None
    carrier: str | None = None  # оператор ДИАПАЗОНА, а не фактический (см. MNP)
    time_zones: list[str] = field(default_factory=list)
    decision: str = DROP
    reason: str = ""


@dataclass
class Summary:
    contacts: list[Contact] = field(default_factory=list)
    duplicates_removed: int = 0
    warnings: list[str] = field(default_factory=list)
    by_country: dict[str, int] = field(default_factory=dict)
    by_type: dict[str, int] = field(default_factory=dict)

    @property
    def to_send(self) -> list[Contact]:
        return [c for c in self.contacts if c.decision in (KEEP, FLAG)]

    @property
    def voice_only(self) -> list[Contact]:
        return [c for c in self.contacts if c.decision == VOICE]

    @property
    def dropped(self) -> list[Contact]:
        return [c for c in self.contacts if c.decision == DROP]


def normalize_contact_list(numbers: list[str], default_country: str = "RU") -> Summary:
    """Готовит список номеров к SMS-рассылке: решение по каждому + дедупликация по E.164."""
    summary = Summary()
    seen: dict[str, str] = {}  # E.164 -> первый исходный номер с этим E.164

    for raw in numbers:
        raw = raw.strip()
        if not raw:
            continue

        contact = Contact(raw=raw)

        # countryCode нужен только номеру без «+»: иначе страну определить не из чего.
        country = None if raw.startswith("+") else default_country

        try:
            report = lookup_phone(raw, country)
        except AtloriumError as error:
            # 400 — строка вообще не является телефоном. Такой запрос не тарифицируется.
            contact.reason = (
                "не распознан как телефонный номер" if error.status == 400 else str(error)
            )
            summary.contacts.append(contact)
            continue

        number = report["number"]
        contact.e164 = number.get("e164")
        contact.number_type = number.get("numberType")
        contact.country_code = number.get("countryCode")
        contact.carrier = number.get("originalCarrier")
        contact.time_zones = number.get("timeZones") or []

        # Невалиден — значит, номер не попадает в реально выделенный диапазон своей страны.
        # SMS уйдёт в никуда, а деньги за неё спишут.
        if not number.get("isValid"):
            contact.reason = "номер не попадает в выделенный диапазон"
            summary.contacts.append(contact)
            continue

        kind = (contact.number_type or "").lower()

        if kind == "premiumrate":
            # Премиум-номера тарифицируются по повышенной ставке. Случайная отправка
            # на них бьёт по бюджету — такие номера отсеиваем и говорим об этом вслух.
            contact.reason = "премиум-номер: повышенная тарификация"
            summary.warnings.append(
                f"{raw} — премиум-номер ({contact.e164}). "
                f"Отправка на такие номера тарифицируется по повышенной ставке."
            )
        elif kind in ("tollfree", "sharedcost"):
            contact.reason = "сервисный номер (8-800 и аналоги), SMS не принимает"
        elif kind == "fixedline":
            # Стационарный номер SMS не принимает — но это живой контакт для обзвона.
            contact.decision = VOICE
            contact.reason = "стационарный: не для SMS, но годится для голосового обзвона"
        elif kind == "voip":
            # Виртуальные номера часто используют как одноразовые при верификации.
            contact.decision = FLAG
            contact.reason = "VoIP: повышенный риск фрода / одноразового номера"
        elif kind in ("mobile", "fixedlineormobile"):
            contact.decision = KEEP
            contact.reason = (
                "мобильный"
                if kind == "mobile"
                else "страна не разделяет мобильные и стационарные номера"
            )
        else:
            contact.reason = f"тип {contact.number_type or 'Unknown'}: SMS не отправляем"

        summary.contacts.append(contact)

    # Дедупликация — только среди тех, кто дошёл до рассылки. Два по-разному записанных
    # номера после приведения к E.164 — один и тот же контакт. Это прямая экономия:
    # за каждую лишнюю SMS платит отправитель.
    for contact in summary.contacts:
        if contact.decision not in (KEEP, FLAG, VOICE) or not contact.e164:
            continue
        if contact.e164 in seen:
            contact.decision = DUP
            contact.reason = f"дубль {seen[contact.e164]!r}"
            summary.duplicates_removed += 1
        else:
            seen[contact.e164] = contact.raw

    # Разбивка считается по уникальным разобранным номерам — дубли не удваивают статистику.
    counted: set[str] = set()
    for contact in summary.contacts:
        if not contact.e164 or contact.e164 in counted:
            continue
        counted.add(contact.e164)
        code = contact.country_code or "??"
        kind = contact.number_type or "Unknown"
        summary.by_country[code] = summary.by_country.get(code, 0) + 1
        summary.by_type[kind] = summary.by_type.get(kind, 0) + 1

    return summary


def pad(value: str, width: int) -> str:
    """Дополняет строку пробелами до нужной ширины (минимум один пробел-разделитель)."""
    return value + " " * max(width - len(value), 1)


def main() -> int:
    if API_KEY == SANDBOX_KEY:
        print("Демо-ключ: ответы сгенерированы (моки), не реальные данные.\n")

    numbers = sys.argv[1].split(",") if len(sys.argv) > 1 else DEMO_NUMBERS

    try:
        summary = normalize_contact_list(numbers)
    except AtloriumError as error:
        print(f"Ошибка: {error}", file=sys.stderr)
        return 1

    print(pad("вход", 22) + pad("E.164", 16) + pad("тип", 18) + "решение")
    print("-" * 78)
    for contact in summary.contacts:
        print(
            pad(contact.raw, 22)
            + pad(contact.e164 or "—", 16)
            + pad(contact.number_type or "—", 18)
            + pad(contact.decision, 6)
            + contact.reason
        )

    print()
    for warning in summary.warnings:
        print(f"  [!] {warning}")
    if summary.warnings:
        print()

    print(f"К отправке SMS:        {len(summary.to_send)}")
    print(f"Только для обзвона:    {len(summary.voice_only)}")
    print(f"Отброшено:             {len(summary.dropped)}")
    print(f"Дублей схлопнулось:    {summary.duplicates_removed}")

    countries = ", ".join(f"{code} — {n}" for code, n in sorted(summary.by_country.items()))
    types = ", ".join(f"{kind} — {n}" for kind, n in sorted(summary.by_type.items()))
    print(f"\nПо странам:  {countries}")
    print(f"По типам:    {types}")

    # Часовые пояса нужны, чтобы не разбудить абонента SMS в три часа ночи по его времени.
    print("\nК отправке (E.164 — то, что принимают SMS-шлюзы):")
    for contact in summary.to_send:
        zones = ", ".join(contact.time_zones) or "—"
        print(f"  {contact.e164}  {contact.carrier or '—'}  [{zones}]")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
