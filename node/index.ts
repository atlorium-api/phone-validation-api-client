/**
 * Клиент API валидации телефона Atlorium — разбор номера любой страны.
 *
 * Запуск (работает сразу, без регистрации — на демо-ключе):
 *   npm install
 *   npm start
 *   npm start -- "+79161234567,8 495 785-63-00"
 *
 * Боевой ключ: получить на https://atlorium.com и положить в переменную окружения
 * ATLORIUM_API_KEY. Код при этом не меняется.
 *
 * ГРАНИЦА СЕРВИСА. Разбор идёт по встроенному справочнику мировой нумерации, локально,
 * без обращения в сеть оператора. Сервис отвечает на вопрос «может ли такой номер
 * существовать и как он правильно записывается», но НЕ проверяет, пользуется ли номером
 * живой абонент. «Валидный» не значит «рабочий».
 */

/**
 * Публичный демо-ключ. С ним API отвечает правдоподобными МОКАМИ (не реальными
 * данными) — чтобы можно было встроить и протестировать интеграцию до оплаты.
 * Ответы детерминированы: один и тот же запрос всегда даёт один и тот же результат,
 * поэтому на них можно писать стабильные тесты.
 */
const SANDBOX_KEY = 'ak_sandbox_demo_mockdata_v1';

const API_KEY = process.env.ATLORIUM_API_KEY ?? SANDBOX_KEY;
const BASE_URL = process.env.ATLORIUM_BASE_URL ?? 'https://atlorium.com';

const TIMEOUT_MS = 30_000;

/** Номера в разнобойных форматах — так и выглядит выгрузка из реальной CRM. */
const DEMO_NUMBERS = [
  '+79161234567',
  '+7 (916) 123-45-67',
  '8 916 123-45-67',
  '+7 495 785-63-00',
  '+7 809 123-45-67',
  '+1 650 253 0000',
  '12345',
];

/** Тип номера, определённый по его диапазону в справочнике нумерации. */
export type PhoneNumberKind =
  | 'Unknown'
  | 'Mobile'
  | 'FixedLine'
  | 'FixedLineOrMobile'
  | 'TollFree'
  | 'PremiumRate'
  | 'SharedCost'
  | 'Voip'
  | 'PersonalNumber'
  | 'Pager'
  | 'Uan'
  | 'Voicemail';

/** Результат офлайн-разбора номера. */
export interface PhoneNumberInfo {
  /** Номер попадает в реально выделенный диапазон своей страны. Не значит «за ним есть абонент». */
  isValid: boolean;
  /** Номер лишь похож на телефонный (подходящая длина). Более мягкая проверка, чем isValid. */
  isPossible: boolean;
  /** Канонический формат. Именно его стоит хранить в базе и отдавать SMS-шлюзу. */
  e164: string | null;
  international: string | null;
  national: string | null;
  rfc3966: string | null;
  countryCallingCode: number;
  countryCode: string | null;
  country: string | null;
  numberType: PhoneNumberKind;
  /** Оператор ДИАПАЗОНА, а не фактический: номер мог быть перенесён по MNP. */
  originalCarrier: string | null;
  /** Привязка ДИАПАЗОНА, а не местоположение абонента. У мобильных обычно null. */
  location: string | null;
  timeZones: string[];
}

export interface PhoneReport {
  input: string;
  number: PhoneNumberInfo;
  elapsedMs: number;
}

const ERROR_REASONS: Record<number, string> = {
  400: 'Строка не является телефонным номером (или не указана страна для номера без «+»)',
  401: 'API-ключ отсутствует, просрочен или недействителен',
  402: 'Недостаточно кредитов на балансе — пополните на https://atlorium.com',
  429: 'Превышен лимит запросов — повторите позже',
  503: 'Сервис временно недоступен (за сбой на своей стороне мы не списываем деньги)',
};

/** Ошибка API: HTTP-код разложен в человекочитаемую причину. */
export class AtloriumError extends Error {
  constructor(readonly status: number, readonly body: string) {
    super(`HTTP ${status}: ${ERROR_REASONS[status] ?? 'Неизвестная ошибка'}`);
    this.name = 'AtloriumError';
  }
}

/**
 * Карточка номера: валидность, форматы записи, страна, тип, оператор диапазона, часовые пояса.
 *
 * @param number Номер в международном ("+79161234567") или национальном ("8 916 123-45-67")
 *   формате. Пробелы, скобки и дефисы допустимы.
 * @param countryCode ISO-3166 alpha-2 ("RU"): в какой стране трактовать номер, записанный
 *   БЕЗ «+». Для номеров с «+» не нужен и игнорируется.
 */
export async function lookupPhone(number: string, countryCode?: string): Promise<PhoneReport> {
  // ВАЖНО: номер идёт в ПУТИ, поэтому кодируем его целиком. «+» обязан превратиться
  // в %2B — иначе часть веб-серверов и прокси прочитает его как пробел, номер потеряет
  // код страны и разбор развалится.
  const url = new URL(`/api/Phone/${encodeURIComponent(number)}`, BASE_URL);
  if (countryCode) {
    url.searchParams.set('countryCode', countryCode);
  }

  const response = await fetch(url, {
    headers: {
      Authorization: `Bearer ${API_KEY}`,
      Accept: 'application/json',
    },
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });

  if (!response.ok) {
    throw new AtloriumError(response.status, await response.text());
  }
  return (await response.json()) as PhoneReport;
}

// ── Применение данных: подготовка базы контактов к SMS-рассылке ───────────────
// Карточка номера сама по себе — просто JSON. Ценность появляется, когда по ней
// принимают решение. Ниже — то, что реально делают перед рассылкой: отсев номеров,
// на которые SMS не дойдёт или дойдёт дорого, приведение к E.164 и дедупликация.

/** Решения по номеру. */
export type Decision =
  | 'KEEP' // в рассылку
  | 'FLAG' // в рассылку, но с пометкой риска
  | 'DUP' // дубль: тот же номер уже есть в списке
  | 'VOICE' // SMS не примет, но годится для голосового обзвона
  | 'DROP'; // выбросить

export interface Contact {
  /** Как номер был записан на входе. */
  raw: string;
  /** Канонический формат — единственный, который принимают SMS-шлюзы. */
  e164: string | null;
  numberType: PhoneNumberKind | null;
  countryCode: string | null;
  /** Оператор ДИАПАЗОНА, а не фактический (см. MNP). */
  carrier: string | null;
  timeZones: string[];
  decision: Decision;
  reason: string;
}

export interface Summary {
  contacts: Contact[];
  duplicatesRemoved: number;
  warnings: string[];
  byCountry: Map<string, number>;
  byType: Map<string, number>;
}

const toSend = (summary: Summary): Contact[] =>
  summary.contacts.filter((c) => c.decision === 'KEEP' || c.decision === 'FLAG');

const voiceOnly = (summary: Summary): Contact[] =>
  summary.contacts.filter((c) => c.decision === 'VOICE');

const dropped = (summary: Summary): Contact[] =>
  summary.contacts.filter((c) => c.decision === 'DROP');

/** Готовит список номеров к SMS-рассылке: решение по каждому + дедупликация по E.164. */
export async function normalizeContactList(
  numbers: string[],
  defaultCountry = 'RU',
): Promise<Summary> {
  const summary: Summary = {
    contacts: [],
    duplicatesRemoved: 0,
    warnings: [],
    byCountry: new Map(),
    byType: new Map(),
  };

  for (const input of numbers) {
    const raw = input.trim();
    if (!raw) continue;

    const contact: Contact = {
      raw,
      e164: null,
      numberType: null,
      countryCode: null,
      carrier: null,
      timeZones: [],
      decision: 'DROP',
      reason: '',
    };

    // countryCode нужен только номеру без «+»: иначе страну определить не из чего.
    const country = raw.startsWith('+') ? undefined : defaultCountry;

    let report: PhoneReport;
    try {
      report = await lookupPhone(raw, country);
    } catch (error) {
      // 400 — строка вообще не является телефоном. Такой запрос не тарифицируется.
      if (error instanceof AtloriumError && error.status === 400) {
        contact.reason = 'не распознан как телефонный номер';
        summary.contacts.push(contact);
        continue;
      }
      throw error;
    }

    const number = report.number;
    contact.e164 = number.e164;
    contact.numberType = number.numberType;
    contact.countryCode = number.countryCode;
    contact.carrier = number.originalCarrier;
    contact.timeZones = number.timeZones ?? [];

    // Невалиден — значит, номер не попадает в реально выделенный диапазон своей страны.
    // SMS уйдёт в никуда, а деньги за неё спишут.
    if (!number.isValid) {
      contact.reason = 'номер не попадает в выделенный диапазон';
      summary.contacts.push(contact);
      continue;
    }

    switch (number.numberType) {
      case 'PremiumRate':
        // Премиум-номера тарифицируются по повышенной ставке. Случайная отправка
        // на них бьёт по бюджету — такие номера отсеиваем и говорим об этом вслух.
        contact.reason = 'премиум-номер: повышенная тарификация';
        summary.warnings.push(
          `${raw} — премиум-номер (${contact.e164}). ` +
            'Отправка на такие номера тарифицируется по повышенной ставке.',
        );
        break;

      case 'TollFree':
      case 'SharedCost':
        contact.reason = 'сервисный номер (8-800 и аналоги), SMS не принимает';
        break;

      case 'FixedLine':
        // Стационарный номер SMS не принимает — но это живой контакт для обзвона.
        contact.decision = 'VOICE';
        contact.reason = 'стационарный: не для SMS, но годится для голосового обзвона';
        break;

      case 'Voip':
        // Виртуальные номера часто используют как одноразовые при верификации.
        contact.decision = 'FLAG';
        contact.reason = 'VoIP: повышенный риск фрода / одноразового номера';
        break;

      case 'Mobile':
        contact.decision = 'KEEP';
        contact.reason = 'мобильный';
        break;

      case 'FixedLineOrMobile':
        contact.decision = 'KEEP';
        contact.reason = 'страна не разделяет мобильные и стационарные номера';
        break;

      default:
        contact.reason = `тип ${number.numberType}: SMS не отправляем`;
        break;
    }

    summary.contacts.push(contact);
  }

  // Дедупликация — только среди тех, кто дошёл до рассылки. Два по-разному записанных
  // номера после приведения к E.164 — один и тот же контакт. Это прямая экономия:
  // за каждую лишнюю SMS платит отправитель.
  const seen = new Map<string, string>();
  for (const contact of summary.contacts) {
    if (!contact.e164) continue;
    if (contact.decision !== 'KEEP' && contact.decision !== 'FLAG' && contact.decision !== 'VOICE') {
      continue;
    }

    const first = seen.get(contact.e164);
    if (first !== undefined) {
      contact.decision = 'DUP';
      contact.reason = `дубль '${first}'`;
      summary.duplicatesRemoved += 1;
    } else {
      seen.set(contact.e164, contact.raw);
    }
  }

  // Разбивка считается по уникальным разобранным номерам — дубли не удваивают статистику.
  const counted = new Set<string>();
  for (const contact of summary.contacts) {
    if (!contact.e164 || counted.has(contact.e164)) continue;
    counted.add(contact.e164);

    const code = contact.countryCode ?? '??';
    const kind = contact.numberType ?? 'Unknown';
    summary.byCountry.set(code, (summary.byCountry.get(code) ?? 0) + 1);
    summary.byType.set(kind, (summary.byType.get(kind) ?? 0) + 1);
  }

  return summary;
}

/** Дополняет строку пробелами до нужной ширины (минимум один пробел-разделитель). */
const pad = (value: string, width: number): string =>
  value + ' '.repeat(Math.max(width - value.length, 1));

const breakdown = (counts: Map<string, number>): string =>
  [...counts.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([key, n]) => `${key} — ${n}`)
    .join(', ');

async function main(): Promise<void> {
  if (API_KEY === SANDBOX_KEY) {
    console.log('Демо-ключ: ответы сгенерированы (моки), не реальные данные.\n');
  }

  const argument = process.argv[2];
  const numbers = argument ? argument.split(',') : DEMO_NUMBERS;

  const summary = await normalizeContactList(numbers);

  console.log(`${pad('вход', 22)}${pad('E.164', 16)}${pad('тип', 18)}решение`);
  console.log('-'.repeat(78));
  for (const contact of summary.contacts) {
    console.log(
      pad(contact.raw, 22) +
        pad(contact.e164 ?? '—', 16) +
        pad(contact.numberType ?? '—', 18) +
        pad(contact.decision, 6) +
        contact.reason,
    );
  }

  console.log();
  for (const warning of summary.warnings) {
    console.log(`  [!] ${warning}`);
  }
  if (summary.warnings.length > 0) console.log();

  console.log(`К отправке SMS:        ${toSend(summary).length}`);
  console.log(`Только для обзвона:    ${voiceOnly(summary).length}`);
  console.log(`Отброшено:             ${dropped(summary).length}`);
  console.log(`Дублей схлопнулось:    ${summary.duplicatesRemoved}`);

  console.log(`\nПо странам:  ${breakdown(summary.byCountry)}`);
  console.log(`По типам:    ${breakdown(summary.byType)}`);

  // Часовые пояса нужны, чтобы не разбудить абонента SMS в три часа ночи по его времени.
  console.log('\nК отправке (E.164 — то, что принимают SMS-шлюзы):');
  for (const contact of toSend(summary)) {
    const zones = contact.timeZones.join(', ') || '—';
    console.log(`  ${contact.e164}  ${contact.carrier ?? '—'}  [${zones}]`);
  }
}

// Запуск только когда файл выполняется напрямую, а не импортируется.
if (process.argv[1]?.includes('index')) {
  main().catch((error: unknown) => {
    console.error('Ошибка:', error instanceof Error ? error.message : error);
    process.exit(1);
  });
}
