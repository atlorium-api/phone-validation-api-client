/*
 * Клиент API валидации телефона Atlorium — разбор номера любой страны.
 *
 * Запуск (работает сразу, без регистрации — на демо-ключе).
 * Начиная с Java 11 файл запускается напрямую, без компиляции и без зависимостей:
 *
 *     java Main.java
 *     java Main.java "+79161234567,8 495 785-63-00"
 *
 * Боевой ключ: получить на https://atlorium.com и положить в переменную окружения
 * ATLORIUM_API_KEY. Код при этом не меняется.
 *
 * ГРАНИЦА СЕРВИСА. Разбор идёт по встроенному справочнику мировой нумерации, локально,
 * без обращения в сеть оператора. Сервис отвечает на вопрос «может ли такой номер
 * существовать и как он правильно записывается», но НЕ проверяет, пользуется ли номером
 * живой абонент. «Валидный» не значит «рабочий».
 */

import java.io.IOException;
import java.net.URI;
import java.net.URLEncoder;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Collectors;

public class Main {

    /**
     * Публичный демо-ключ. С ним API отвечает правдоподобными МОКАМИ (не реальными
     * данными) — чтобы можно было встроить и протестировать интеграцию до оплаты.
     * Ответы детерминированы: один и тот же запрос всегда даёт один и тот же результат,
     * поэтому на них можно писать стабильные тесты.
     */
    static final String SANDBOX_KEY = "ak_sandbox_demo_mockdata_v1";

    static final String API_KEY = envOr("ATLORIUM_API_KEY", SANDBOX_KEY);
    static final String BASE_URL = envOr("ATLORIUM_BASE_URL", "https://atlorium.com");

    /** Номера в разнобойных форматах — так и выглядит выгрузка из реальной CRM. */
    static final List<String> DEMO_NUMBERS = List.of(
            "+79161234567",
            "+7 (916) 123-45-67",
            "8 916 123-45-67",
            "+7 495 785-63-00",
            "+7 809 123-45-67",
            "+1 650 253 0000",
            "12345");

    static final HttpClient CLIENT = HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(30))
            .build();

    static String envOr(String key, String fallback) {
        String value = System.getenv(key);
        return (value == null || value.isBlank()) ? fallback : value;
    }

    /** Ошибка API: HTTP-код разложен в человекочитаемую причину. */
    static class AtloriumException extends RuntimeException {
        private static final Map<Integer, String> REASONS = Map.of(
                400, "Строка не является телефонным номером (или не указана страна для номера без «+»)",
                401, "API-ключ отсутствует, просрочен или недействителен",
                402, "Недостаточно кредитов на балансе — пополните на https://atlorium.com",
                429, "Превышен лимит запросов — повторите позже",
                503, "Сервис временно недоступен (за сбой на своей стороне мы не списываем деньги)");

        final int status;

        AtloriumException(int status) {
            super("HTTP " + status + ": " + REASONS.getOrDefault(status, "Неизвестная ошибка"));
            this.status = status;
        }
    }

    /**
     * Кодирует номер для подстановки в ПУТЬ запроса.
     *
     * URLEncoder работает по правилам HTML-формы: он превращает «+» в %2B (это нам и нужно),
     * но пробел — в «+», что в пути означало бы литеральный плюс. Поэтому пробелы дожимаем
     * до %20 вручную. Уже закодированный %2B при этом не страдает.
     *
     * Если «+» уедет на сервер как есть, часть веб-серверов и прокси прочитает его как
     * пробел: номер потеряет код страны и разбор развалится.
     */
    static String escapePath(String value) {
        return URLEncoder.encode(value, StandardCharsets.UTF_8).replace("+", "%20");
    }

    /**
     * Карточка номера: валидность, форматы записи, страна, тип, оператор диапазона, часовые пояса.
     *
     * @param number      номер в международном ("+79161234567") или национальном ("8 916 123-45-67")
     *                    формате; пробелы, скобки и дефисы допустимы
     * @param countryCode ISO-3166 alpha-2 ("RU"): в какой стране трактовать номер, записанный БЕЗ «+».
     *                    Для номеров с «+» не нужен и игнорируется; передавайте null
     */
    static String lookupPhone(String number, String countryCode) throws IOException, InterruptedException {
        String url = BASE_URL + "/api/Phone/" + escapePath(number);
        if (countryCode != null && !countryCode.isBlank()) {
            url += "?countryCode=" + URLEncoder.encode(countryCode, StandardCharsets.UTF_8);
        }

        HttpRequest request = HttpRequest.newBuilder(URI.create(url))
                .header("Authorization", "Bearer " + API_KEY)
                .header("Accept", "application/json")
                .timeout(Duration.ofSeconds(30))
                .GET()
                .build();

        HttpResponse<String> response = CLIENT.send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() != 200) {
            throw new AtloriumException(response.statusCode());
        }
        return response.body();
    }

    // ── Разбор JSON ──────────────────────────────────────────────────────────
    // Пример намеренно оставлен без внешних зависимостей, чтобы запускаться одной
    // командой `java Main.java`. В рабочем проекте берите Jackson или Gson и маппьте
    // ответ в полноценную запись — эти регулярки существуют только ради отсутствия pom.xml.
    //
    // Полезная нагрузка лежит во вложенном объекте `number`, поэтому сначала вырезаем его,
    // а потом уже ищем поля внутри: иначе, например, поле `input` верхнего уровня
    // перепуталось бы с полями карточки.

    static String object(String json, String field) {
        Matcher matcher = Pattern.compile("\"" + field + "\"\\s*:\\s*\\{").matcher(json);
        if (!matcher.find()) {
            return "";
        }
        // Идём от открывающей скобки и считаем вложенность, пока она не обнулится.
        int depth = 0;
        int start = matcher.end() - 1;
        for (int i = start; i < json.length(); i++) {
            char c = json.charAt(i);
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return json.substring(start, i + 1);
        }
        return "";
    }

    static String str(String json, String field) {
        Matcher matcher = Pattern.compile("\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").matcher(json);
        return matcher.find() ? matcher.group(1).replace("\\\"", "\"") : null;
    }

    static boolean bool(String json, String field) {
        Matcher matcher = Pattern.compile("\"" + field + "\"\\s*:\\s*(true|false)").matcher(json);
        return matcher.find() && "true".equals(matcher.group(1));
    }

    /** Массив строк: "timeZones": ["Europe/Moscow", "Asia/Yekaterinburg"]. */
    static List<String> strings(String json, String field) {
        List<String> values = new ArrayList<>();
        Matcher array = Pattern.compile("\"" + field + "\"\\s*:\\s*\\[(.*?)\\]", Pattern.DOTALL)
                .matcher(json);
        if (!array.find()) {
            return values;
        }
        Matcher item = Pattern.compile("\"((?:[^\"\\\\]|\\\\.)*)\"").matcher(array.group(1));
        while (item.find()) {
            values.add(item.group(1));
        }
        return values;
    }

    // ── Применение данных: подготовка базы контактов к SMS-рассылке ───────────
    // Карточка номера сама по себе — просто JSON. Ценность появляется, когда по ней
    // принимают решение. Ниже — то, что реально делают перед рассылкой: отсев номеров,
    // на которые SMS не дойдёт или дойдёт дорого, приведение к E.164 и дедупликация.

    // Решения по номеру.
    static final String KEEP = "KEEP";   // в рассылку
    static final String FLAG = "FLAG";   // в рассылку, но с пометкой риска
    static final String DUP = "DUP";     // дубль: тот же номер уже есть в списке
    static final String VOICE = "VOICE"; // SMS не примет, но годится для голосового обзвона
    static final String DROP = "DROP";   // выбросить

    /** Итог разбора одного номера из списка. */
    static final class Contact {
        final String raw;             // как номер был записан на входе
        String e164;                  // канонический формат — его и принимают SMS-шлюзы
        String numberType;
        String countryCode;
        String carrier;               // оператор ДИАПАЗОНА, а не фактический (см. MNP)
        List<String> timeZones = List.of();
        String decision = DROP;
        String reason = "";

        Contact(String raw) {
            this.raw = raw;
        }
    }

    /** Сводка по всему списку. */
    static final class Summary {
        final List<Contact> contacts = new ArrayList<>();
        final List<String> warnings = new ArrayList<>();
        final Map<String, Integer> byCountry = new TreeMap<>();
        final Map<String, Integer> byType = new TreeMap<>();
        int duplicatesRemoved;

        List<Contact> withDecision(String... decisions) {
            List<String> wanted = List.of(decisions);
            return contacts.stream().filter(c -> wanted.contains(c.decision)).collect(Collectors.toList());
        }
    }

    /** Готовит список номеров к SMS-рассылке: решение по каждому + дедупликация по E.164. */
    static Summary normalizeContactList(List<String> numbers, String defaultCountry)
            throws IOException, InterruptedException {

        Summary summary = new Summary();

        for (String input : numbers) {
            String raw = input.trim();
            if (raw.isEmpty()) {
                continue;
            }

            Contact contact = new Contact(raw);

            // countryCode нужен только номеру без «+»: иначе страну определить не из чего.
            String country = raw.startsWith("+") ? null : defaultCountry;

            String json;
            try {
                json = lookupPhone(raw, country);
            } catch (AtloriumException error) {
                // 400 — строка вообще не является телефоном. Такой запрос не тарифицируется.
                if (error.status != 400) {
                    throw error;
                }
                contact.reason = "не распознан как телефонный номер";
                summary.contacts.add(contact);
                continue;
            }

            // Полезная нагрузка вложена в объект `number` — работаем именно с ним.
            String number = object(json, "number");
            contact.e164 = str(number, "e164");
            contact.numberType = str(number, "numberType");
            contact.countryCode = str(number, "countryCode");
            contact.carrier = str(number, "originalCarrier");
            contact.timeZones = strings(number, "timeZones");

            // Невалиден — значит, номер не попадает в реально выделенный диапазон своей
            // страны. SMS уйдёт в никуда, а деньги за неё спишут.
            if (!bool(number, "isValid")) {
                contact.reason = "номер не попадает в выделенный диапазон";
                summary.contacts.add(contact);
                continue;
            }

            String kind = contact.numberType == null ? "Unknown" : contact.numberType;
            switch (kind) {
                case "PremiumRate" -> {
                    // Премиум-номера тарифицируются по повышенной ставке. Случайная отправка
                    // на них бьёт по бюджету — отсеиваем и говорим об этом вслух.
                    contact.reason = "премиум-номер: повышенная тарификация";
                    summary.warnings.add(raw + " — премиум-номер (" + contact.e164 + "). "
                            + "Отправка на такие номера тарифицируется по повышенной ставке.");
                }
                case "TollFree", "SharedCost" ->
                        contact.reason = "сервисный номер (8-800 и аналоги), SMS не принимает";
                case "FixedLine" -> {
                    // Стационарный номер SMS не принимает — но это живой контакт для обзвона.
                    contact.decision = VOICE;
                    contact.reason = "стационарный: не для SMS, но годится для голосового обзвона";
                }
                case "Voip" -> {
                    // Виртуальные номера часто используют как одноразовые при верификации.
                    contact.decision = FLAG;
                    contact.reason = "VoIP: повышенный риск фрода / одноразового номера";
                }
                case "Mobile" -> {
                    contact.decision = KEEP;
                    contact.reason = "мобильный";
                }
                case "FixedLineOrMobile" -> {
                    contact.decision = KEEP;
                    contact.reason = "страна не разделяет мобильные и стационарные номера";
                }
                default -> contact.reason = "тип " + kind + ": SMS не отправляем";
            }

            summary.contacts.add(contact);
        }

        // Дедупликация — только среди тех, кто дошёл до рассылки. Два по-разному записанных
        // номера после приведения к E.164 — один и тот же контакт. Это прямая экономия:
        // за каждую лишнюю SMS платит отправитель.
        Map<String, String> seen = new LinkedHashMap<>();
        for (Contact contact : summary.contacts) {
            if (contact.e164 == null) {
                continue;
            }
            if (!KEEP.equals(contact.decision) && !FLAG.equals(contact.decision)
                    && !VOICE.equals(contact.decision)) {
                continue;
            }

            String first = seen.get(contact.e164);
            if (first != null) {
                contact.decision = DUP;
                contact.reason = "дубль '" + first + "'";
                summary.duplicatesRemoved++;
            } else {
                seen.put(contact.e164, contact.raw);
            }
        }

        // Разбивка считается по уникальным разобранным номерам — дубли не удваивают статистику.
        List<String> counted = new ArrayList<>();
        for (Contact contact : summary.contacts) {
            if (contact.e164 == null || counted.contains(contact.e164)) {
                continue;
            }
            counted.add(contact.e164);

            String code = contact.countryCode == null ? "??" : contact.countryCode;
            String kind = contact.numberType == null ? "Unknown" : contact.numberType;
            summary.byCountry.merge(code, 1, Integer::sum);
            summary.byType.merge(kind, 1, Integer::sum);
        }

        return summary;
    }

    /** Дополняет строку пробелами до нужной ширины (минимум один пробел-разделитель). */
    static String pad(String value, int width) {
        int gap = Math.max(width - value.length(), 1);
        return value + " ".repeat(gap);
    }

    static String breakdown(Map<String, Integer> counts) {
        return counts.entrySet().stream()
                .map(entry -> entry.getKey() + " — " + entry.getValue())
                .collect(Collectors.joining(", "));
    }

    static String orDash(String value) {
        return (value == null || value.isEmpty()) ? "—" : value;
    }

    public static void main(String[] args) throws Exception {
        if (API_KEY.equals(SANDBOX_KEY)) {
            System.out.println("Демо-ключ: ответы сгенерированы (моки), не реальные данные.\n");
        }

        List<String> numbers = args.length > 0 ? List.of(args[0].split(",")) : DEMO_NUMBERS;

        Summary summary;
        try {
            summary = normalizeContactList(numbers, "RU");
        } catch (AtloriumException error) {
            System.err.println("Ошибка: " + error.getMessage());
            System.exit(1);
            return;
        }

        System.out.println(pad("вход", 22) + pad("E.164", 16) + pad("тип", 18) + "решение");
        System.out.println("-".repeat(78));
        for (Contact contact : summary.contacts) {
            System.out.println(pad(contact.raw, 22)
                    + pad(orDash(contact.e164), 16)
                    + pad(orDash(contact.numberType), 18)
                    + pad(contact.decision, 6)
                    + contact.reason);
        }

        System.out.println();
        summary.warnings.forEach(warning -> System.out.println("  [!] " + warning));
        if (!summary.warnings.isEmpty()) {
            System.out.println();
        }

        System.out.println("К отправке SMS:        " + summary.withDecision(KEEP, FLAG).size());
        System.out.println("Только для обзвона:    " + summary.withDecision(VOICE).size());
        System.out.println("Отброшено:             " + summary.withDecision(DROP).size());
        System.out.println("Дублей схлопнулось:    " + summary.duplicatesRemoved);

        System.out.println("\nПо странам:  " + breakdown(summary.byCountry));
        System.out.println("По типам:    " + breakdown(summary.byType));

        // Часовые пояса нужны, чтобы не разбудить абонента SMS в три часа ночи по его времени.
        System.out.println("\nК отправке (E.164 — то, что принимают SMS-шлюзы):");
        for (Contact contact : summary.withDecision(KEEP, FLAG)) {
            String zones = contact.timeZones.isEmpty() ? "—" : String.join(", ", contact.timeZones);
            System.out.println("  " + contact.e164 + "  " + orDash(contact.carrier) + "  [" + zones + "]");
        }
    }
}
