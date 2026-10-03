// Клиент API валидации телефона Atlorium — разбор номера любой страны.
//
// Запуск (работает сразу, без регистрации — на демо-ключе):
//
//	go run .
//	go run . "+79161234567,8 495 785-63-00"
//
// Боевой ключ: получить на https://atlorium.com и положить в переменную окружения
// ATLORIUM_API_KEY. Код при этом не меняется.
//
// ГРАНИЦА СЕРВИСА. Разбор идёт по встроенному справочнику мировой нумерации, локально,
// без обращения в сеть оператора. Сервис отвечает на вопрос «может ли такой номер
// существовать и как он правильно записывается», но НЕ проверяет, пользуется ли номером
// живой абонент. «Валидный» не значит «рабочий».
package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"sort"
	"strings"
	"time"
)

// SandboxKey — публичный демо-ключ. С ним API отвечает правдоподобными МОКАМИ
// (не реальными данными), чтобы можно было встроить интеграцию до оплаты.
// Ответы детерминированы — на них можно писать стабильные тесты.
const SandboxKey = "ak_sandbox_demo_mockdata_v1"

var (
	apiKey  = envOr("ATLORIUM_API_KEY", SandboxKey)
	baseURL = envOr("ATLORIUM_BASE_URL", "https://atlorium.com")
	client  = &http.Client{Timeout: 30 * time.Second}
)

// DemoNumbers — номера в разнобойных форматах: так и выглядит выгрузка из реальной CRM.
var DemoNumbers = []string{
	"+79161234567",
	"+7 (916) 123-45-67",
	"8 916 123-45-67",
	"+7 495 785-63-00",
	"+7 809 123-45-67",
	"+1 650 253 0000",
	"12345",
}

func envOr(key, fallback string) string {
	if value := os.Getenv(key); value != "" {
		return value
	}
	return fallback
}

// PhoneNumberInfo — результат офлайн-разбора номера.
type PhoneNumberInfo struct {
	// IsValid: номер попадает в реально выделенный диапазон своей страны.
	// Это НЕ значит, что за номером есть живой абонент.
	IsValid bool `json:"isValid"`
	// IsPossible: номер лишь похож на телефонный (подходящая длина). Мягче, чем IsValid.
	IsPossible bool `json:"isPossible"`
	// E164 — канонический формат. Именно его стоит хранить в базе и отдавать SMS-шлюзу.
	E164               string `json:"e164"`
	International      string `json:"international"`
	National           string `json:"national"`
	Rfc3966            string `json:"rfc3966"`
	CountryCallingCode int    `json:"countryCallingCode"`
	CountryCode        string `json:"countryCode"`
	Country            string `json:"country"`
	// NumberType: Mobile, FixedLine, FixedLineOrMobile, TollFree, PremiumRate,
	// SharedCost, Voip, PersonalNumber, Pager, Uan, Voicemail, Unknown.
	NumberType string `json:"numberType"`
	// OriginalCarrier — оператор ДИАПАЗОНА, а не фактический: номер мог быть перенесён по MNP.
	OriginalCarrier string `json:"originalCarrier"`
	// Location — привязка ДИАПАЗОНА, а не местоположение абонента. У мобильных обычно пусто.
	Location  string   `json:"location"`
	TimeZones []string `json:"timeZones"`
}

// PhoneReport — карточка результата валидации номера.
type PhoneReport struct {
	Input     string          `json:"input"`
	Number    PhoneNumberInfo `json:"number"`
	ElapsedMs int64           `json:"elapsedMs"`
}

// APIError раскладывает HTTP-код в человекочитаемую причину.
type APIError struct {
	Status int
	Body   string
}

func (e *APIError) Error() string {
	reasons := map[int]string{
		400: "строка не является телефонным номером (или не указана страна для номера без «+»)",
		401: "API-ключ отсутствует, просрочен или недействителен",
		402: "недостаточно кредитов на балансе — пополните на https://atlorium.com",
		429: "превышен лимит запросов — повторите позже",
		503: "сервис временно недоступен (за сбой на своей стороне мы не списываем деньги)",
	}
	reason, ok := reasons[e.Status]
	if !ok {
		reason = "неизвестная ошибка"
	}
	return fmt.Sprintf("HTTP %d: %s", e.Status, reason)
}

// escapePath кодирует номер для подстановки в ПУТЬ запроса.
//
// url.PathEscape оставляет «+» как есть, а нам нужен именно %2B: часть веб-серверов и
// прокси прочитает голый «+» как пробел, номер потеряет код страны и разбор развалится.
func escapePath(value string) string {
	return strings.ReplaceAll(url.PathEscape(value), "+", "%2B")
}

// LookupPhone возвращает карточку номера: валидность, форматы записи, страну, тип,
// оператора диапазона и часовые пояса.
//
// number — в международном формате ("+79161234567") или национальном ("8 916 123-45-67").
// Пробелы, скобки и дефисы допустимы.
//
// countryCode — ISO-3166 alpha-2 ("RU"): в какой стране трактовать номер, записанный
// БЕЗ «+». Для номеров с «+» не нужен и игнорируется.
func LookupPhone(number, countryCode string) (*PhoneReport, error) {
	endpoint := baseURL + "/api/Phone/" + escapePath(number)
	if countryCode != "" {
		endpoint += "?" + url.Values{"countryCode": {countryCode}}.Encode()
	}

	request, err := http.NewRequest(http.MethodGet, endpoint, nil)
	if err != nil {
		return nil, err
	}
	request.Header.Set("Authorization", "Bearer "+apiKey)
	request.Header.Set("Accept", "application/json")

	response, err := client.Do(request)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()

	body, err := io.ReadAll(response.Body)
	if err != nil {
		return nil, err
	}
	if response.StatusCode != http.StatusOK {
		return nil, &APIError{Status: response.StatusCode, Body: string(body)}
	}

	var report PhoneReport
	if err := json.Unmarshal(body, &report); err != nil {
		return nil, err
	}
	return &report, nil
}

// ── Применение данных: подготовка базы контактов к SMS-рассылке ───────────────
// Карточка номера сама по себе — просто JSON. Ценность появляется, когда по ней
// принимают решение. Ниже — то, что реально делают перед рассылкой: отсев номеров,
// на которые SMS не дойдёт или дойдёт дорого, приведение к E.164 и дедупликация.

// Решения по номеру.
const (
	Keep  = "KEEP"  // в рассылку
	Flag  = "FLAG"  // в рассылку, но с пометкой риска
	Dup   = "DUP"   // дубль: тот же номер уже есть в списке
	Voice = "VOICE" // SMS не примет, но годится для голосового обзвона
	Drop  = "DROP"  // выбросить
)

// Contact — итог разбора одного номера из списка.
type Contact struct {
	Raw         string // как номер был записан на входе
	E164        string // канонический формат — единственный, который принимают SMS-шлюзы
	NumberType  string
	CountryCode string
	Carrier     string // оператор ДИАПАЗОНА, а не фактический (см. MNP)
	TimeZones   []string
	Decision    string
	Reason      string
}

// Summary — сводка по всему списку.
type Summary struct {
	Contacts          []*Contact
	DuplicatesRemoved int
	Warnings          []string
	ByCountry         map[string]int
	ByType            map[string]int
}

func (s *Summary) pick(decisions ...string) []*Contact {
	var picked []*Contact
	for _, contact := range s.Contacts {
		for _, decision := range decisions {
			if contact.Decision == decision {
				picked = append(picked, contact)
				break
			}
		}
	}
	return picked
}

// ToSend — контакты, которые пойдут в SMS-рассылку.
func (s *Summary) ToSend() []*Contact { return s.pick(Keep, Flag) }

// VoiceOnly — контакты, пригодные только для голосового обзвона.
func (s *Summary) VoiceOnly() []*Contact { return s.pick(Voice) }

// Dropped — отброшенные контакты.
func (s *Summary) Dropped() []*Contact { return s.pick(Drop) }

// NormalizeContactList готовит список номеров к SMS-рассылке: решение по каждому
// номеру плюс дедупликация по E.164.
func NormalizeContactList(numbers []string, defaultCountry string) (*Summary, error) {
	summary := &Summary{
		ByCountry: make(map[string]int),
		ByType:    make(map[string]int),
	}

	for _, input := range numbers {
		raw := strings.TrimSpace(input)
		if raw == "" {
			continue
		}

		contact := &Contact{Raw: raw, Decision: Drop}

		// countryCode нужен только номеру без «+»: иначе страну определить не из чего.
		country := defaultCountry
		if strings.HasPrefix(raw, "+") {
			country = ""
		}

		report, err := LookupPhone(raw, country)
		if err != nil {
			// 400 — строка вообще не является телефоном. Такой запрос не тарифицируется.
			var apiError *APIError
			if errors.As(err, &apiError) && apiError.Status == http.StatusBadRequest {
				contact.Reason = "не распознан как телефонный номер"
				summary.Contacts = append(summary.Contacts, contact)
				continue
			}
			return nil, err
		}

		number := report.Number
		contact.E164 = number.E164
		contact.NumberType = number.NumberType
		contact.CountryCode = number.CountryCode
		contact.Carrier = number.OriginalCarrier
		contact.TimeZones = number.TimeZones

		// Невалиден — значит, номер не попадает в реально выделенный диапазон своей
		// страны. SMS уйдёт в никуда, а деньги за неё спишут.
		if !number.IsValid {
			contact.Reason = "номер не попадает в выделенный диапазон"
			summary.Contacts = append(summary.Contacts, contact)
			continue
		}

		switch number.NumberType {
		case "PremiumRate":
			// Премиум-номера тарифицируются по повышенной ставке. Случайная отправка
			// на них бьёт по бюджету — отсеиваем и говорим об этом вслух.
			contact.Reason = "премиум-номер: повышенная тарификация"
			summary.Warnings = append(summary.Warnings, fmt.Sprintf(
				"%s — премиум-номер (%s). Отправка на такие номера тарифицируется по повышенной ставке.",
				raw, contact.E164))

		case "TollFree", "SharedCost":
			contact.Reason = "сервисный номер (8-800 и аналоги), SMS не принимает"

		case "FixedLine":
			// Стационарный номер SMS не принимает — но это живой контакт для обзвона.
			contact.Decision = Voice
			contact.Reason = "стационарный: не для SMS, но годится для голосового обзвона"

		case "Voip":
			// Виртуальные номера часто используют как одноразовые при верификации.
			contact.Decision = Flag
			contact.Reason = "VoIP: повышенный риск фрода / одноразового номера"

		case "Mobile":
			contact.Decision = Keep
			contact.Reason = "мобильный"

		case "FixedLineOrMobile":
			contact.Decision = Keep
			contact.Reason = "страна не разделяет мобильные и стационарные номера"

		default:
			contact.Reason = fmt.Sprintf("тип %s: SMS не отправляем", number.NumberType)
		}

		summary.Contacts = append(summary.Contacts, contact)
	}

	// Дедупликация — только среди тех, кто дошёл до рассылки. Два по-разному записанных
	// номера после приведения к E.164 — один и тот же контакт. Это прямая экономия:
	// за каждую лишнюю SMS платит отправитель.
	seen := make(map[string]string)
	for _, contact := range summary.Contacts {
		if contact.E164 == "" {
			continue
		}
		if contact.Decision != Keep && contact.Decision != Flag && contact.Decision != Voice {
			continue
		}

		if first, ok := seen[contact.E164]; ok {
			contact.Decision = Dup
			contact.Reason = fmt.Sprintf("дубль '%s'", first)
			summary.DuplicatesRemoved++
		} else {
			seen[contact.E164] = contact.Raw
		}
	}

	// Разбивка считается по уникальным разобранным номерам — дубли не удваивают статистику.
	counted := make(map[string]bool)
	for _, contact := range summary.Contacts {
		if contact.E164 == "" || counted[contact.E164] {
			continue
		}
		counted[contact.E164] = true

		code := contact.CountryCode
		if code == "" {
			code = "??"
		}
		kind := contact.NumberType
		if kind == "" {
			kind = "Unknown"
		}
		summary.ByCountry[code]++
		summary.ByType[kind]++
	}

	return summary, nil
}

// pad дополняет строку пробелами до нужной ширины (минимум один пробел-разделитель).
//
// Ширина считается в РУНАХ, а не в байтах: кириллица в UTF-8 занимает два байта,
// и обычный %-22s разъехался бы.
func pad(value string, width int) string {
	gap := width - len([]rune(value))
	if gap < 1 {
		gap = 1
	}
	return value + strings.Repeat(" ", gap)
}

func breakdown(counts map[string]int) string {
	keys := make([]string, 0, len(counts))
	for key := range counts {
		keys = append(keys, key)
	}
	sort.Strings(keys)

	parts := make([]string, 0, len(keys))
	for _, key := range keys {
		parts = append(parts, fmt.Sprintf("%s — %d", key, counts[key]))
	}
	return strings.Join(parts, ", ")
}

func orDash(value string) string {
	if value == "" {
		return "—"
	}
	return value
}

func main() {
	if apiKey == SandboxKey {
		fmt.Println("Демо-ключ: ответы сгенерированы (моки), не реальные данные.")
		fmt.Println()
	}

	numbers := DemoNumbers
	if len(os.Args) > 1 {
		numbers = strings.Split(os.Args[1], ",")
	}

	summary, err := NormalizeContactList(numbers, "RU")
	if err != nil {
		fmt.Fprintln(os.Stderr, "Ошибка:", err)
		os.Exit(1)
	}

	fmt.Printf("%s%s%s%s\n", pad("вход", 22), pad("E.164", 16), pad("тип", 18), "решение")
	fmt.Println(strings.Repeat("-", 78))
	for _, contact := range summary.Contacts {
		fmt.Printf("%s%s%s%s%s\n",
			pad(contact.Raw, 22),
			pad(orDash(contact.E164), 16),
			pad(orDash(contact.NumberType), 18),
			pad(contact.Decision, 6),
			contact.Reason)
	}

	fmt.Println()
	for _, warning := range summary.Warnings {
		fmt.Println("  [!]", warning)
	}
	if len(summary.Warnings) > 0 {
		fmt.Println()
	}

	fmt.Printf("К отправке SMS:        %d\n", len(summary.ToSend()))
	fmt.Printf("Только для обзвона:    %d\n", len(summary.VoiceOnly()))
	fmt.Printf("Отброшено:             %d\n", len(summary.Dropped()))
	fmt.Printf("Дублей схлопнулось:    %d\n", summary.DuplicatesRemoved)

	fmt.Printf("\nПо странам:  %s\n", breakdown(summary.ByCountry))
	fmt.Printf("По типам:    %s\n", breakdown(summary.ByType))

	// Часовые пояса нужны, чтобы не разбудить абонента SMS в три часа ночи по его времени.
	fmt.Println("\nК отправке (E.164 — то, что принимают SMS-шлюзы):")
	for _, contact := range summary.ToSend() {
		zones := strings.Join(contact.TimeZones, ", ")
		fmt.Printf("  %s  %s  [%s]\n", contact.E164, orDash(contact.Carrier), orDash(zones))
	}
}
