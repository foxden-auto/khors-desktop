#!/usr/bin/env python3
"""Единая таблица строк интерфейса KHORS Desktop → src/Khors.App/Resources/Strings.resx (en) и Strings.ru.resx.

Добавляя строку, добавляйте оба языка. Запуск: python3 tools/i18n/strings.py
Полноту по кодам ошибок проверяет tests/Khors.App.Tests.
"""
import os
from xml.sax.saxutils import escape

# ключ: (русский, английский)
STRINGS = {
    "AppTitle": ("KHORS", "KHORS"),
    # Состояние подключения
    "StatusDisconnected": ("Отключено", "Disconnected"),
    "StatusConnecting": ("Подключение…", "Connecting…"),
    "StatusConnected": ("Подключено", "Connected"),
    "StatusDisconnecting": ("Отключение…", "Disconnecting…"),
    "StatusFailed": ("Ошибка подключения", "Connection failed"),
    "ButtonConnect": ("Подключить", "Connect"),
    "ButtonDisconnect": ("Отключить", "Disconnect"),
    "ModeSystemProxy": ("Режим: системный прокси", "Mode: system proxy"),
    "NoProfileSelected": ("Выберите профиль в списке", "Select a profile below"),
    "SessionTimeFormat": ("Время сессии: {0}", "Session time: {0}"),
    "LocalProxyFormat": ("Ядро {2}. Локальный прокси: HTTP 127.0.0.1:{0}, SOCKS5 127.0.0.1:{1}", "Core: {2}. Local proxy: HTTP 127.0.0.1:{0}, SOCKS5 127.0.0.1:{1}"),
    "FailureDetails": ("Лог ядра", "Core log"),
    "ButtonCopyCoreLog": ("Копировать весь лог", "Copy the whole log"),
    "CoreLogCopiedFormat": ("Лог ядра скопирован в буфер обмена: строк {0}. Адреса и ключи в нём замаскированы.", "Core log copied to the clipboard: {0} lines. Addresses and keys are masked."),
    # Профили
    "ProfilesHeader": ("Профили", "Profiles"),
    "ButtonImportClipboard": ("Вставить из буфера", "Paste from clipboard"),
    "ButtonDelete": ("Удалить", "Delete"),
    "EmptyProfilesHint": ("Скопируйте ссылку, адрес подписки или картинку с QR-кодом и нажмите «Вставить из буфера».",
                          "Copy a link, a subscription address or a QR code image and press “Paste from clipboard”."),
    "ImportResultFormat": ("Добавлено: {0}. Уже были: {1}. Не распознано: {2}.", "Added: {0}. Already present: {1}. Not recognized: {2}."),
    "ImportLineErrorFormat": ("Ссылка {0}: {1}", "Link {0}: {1}"),
    "ClipboardEmpty": ("В буфере обмена нет ни ссылки, ни картинки с QR-кодом.", "The clipboard contains neither a link nor a QR code image."),
    "ClipboardNoQr": ("На картинке в буфере обмена не найден QR-код.", "No QR code was found in the clipboard image."),
    "MenuImportFile": ("Из файла…", "From file…"),
    "ImportFileDialogTitle": ("Импорт: QR-код, ссылки или конфиг", "Import: QR code, links or config"),
    "FileTypeImportable": ("Картинки с QR-кодом, ссылки и конфиги", "QR code images, links and configs"),
    "FileTooLarge": ("Файл больше 5 МБ — это не конфиг и не картинка с QR-кодом.", "The file is larger than 5 MB — it is not a config or a QR code image."),
    "FileReadFailed": ("Не удалось прочитать файл.", "Could not read the file."),
    "FileNoQr": ("В файле не найден QR-код.", "No QR code was found in the file."),
    "FileEmpty": ("Файл пустой.", "The file is empty."),
    "ProfileWarningsFormat": ("Предупреждение: {0}", "Warning: {0}"),
    "ProfileUnknownParamsFormat": ("Параметры ссылки, которые KHORS пока не понимает: {0}", "Link parameters KHORS does not understand yet: {0}"),
    # Выбор ядра (контекстное меню профиля); названия ядер не переводятся
    "MenuCore": ("Ядро", "Core"),
    "MenuCopyLink": ("Копировать ссылку", "Copy link"),
    "MenuShowQr": ("Показать QR-код", "Show QR code"),
    "QrWindowTitle": ("QR-код профиля", "Profile QR code"),
    "QrWarning": ("В коде — ключи доступа к серверу. Показывайте его только тем, кому доверяете.",
                  "The code contains server access keys. Show it only to people you trust."),
    "ButtonCopyImage": ("Копировать картинку", "Copy image"),
    "ButtonClose": ("Закрыть", "Close"),
    "QrImageCopied": ("Картинка скопирована в буфер обмена.", "The image was copied to the clipboard."),
    "QrTooLong": ("Ссылка слишком длинная для QR-кода — используйте «Копировать ссылку».", "The link is too long for a QR code — use “Copy link”."),
    "LinkCopiedFormat": ("Ссылка «{0}» скопирована. В ней ключи доступа — передавайте её только тем, кому доверяете.",
                         "The link for “{0}” was copied. It contains access keys — share it only with people you trust."),
    "CoreAutoFormat": ("Автоматически (сейчас {0})", "Automatic (now {0})"),
    "CoreChoiceRejectedFormat": ("Ядро {0} не запустит этот профиль: {1}. Выбор ядра не изменён.",
                                 "The {0} core cannot run this profile: {1}. The core choice was not changed."),
    # Тест задержки (LatencyStatus)
    "ButtonTestLatency": ("Задержка", "Latency"),
    "TestLatencyTooltip": ("Проверить задержку всех профилей", "Test latency of all profiles"),
    "LatencyTesting": ("проверка…", "testing…"),
    "ConnectionLatencyFormat": ("Задержка: {0}", "Latency: {0}"),
    "Latency_Ok": ("{0} мс", "{0} ms"),
    "LatencyTooltipFormat": ("Задержка через туннель по готовому соединению. Первое соединение (с установкой туннеля и TLS): {0} мс", "Latency through the tunnel over an established connection. First connection (tunnel and TLS setup): {0} ms"),
    "Latency_Timeout": ("тайм-аут", "timeout"),
    "Latency_Failed": ("нет ответа", "no response"),
    "Latency_Unsupported": ("не поддерживается", "not supported"),
    # Подписки (SubscriptionUpdateError)
    "SubscriptionsHeaderFormat": ("Подписки ({0})", "Subscriptions ({0})"),
    "ButtonUpdate": ("Обновить", "Update"),
    "SubscriptionAddedFormat": ("Подписка «{0}» добавлена: профилей {1}.", "Subscription “{0}” added: {1} profiles."),
    "SubscriptionUpdatedFormat": ("«{0}» обновлена: профилей {1}, новых {2}, удалено {3}.", "“{0}” updated: {1} profiles, {2} new, {3} removed."),
    "SubscriptionUpdateFailedFormat": ("«{0}» не обновилась: {1}.", "“{0}” was not updated: {1}."),
    "SubscriptionViaProxy": ("загружено через KHORS — напрямую сервер подписки недоступен", "downloaded through KHORS — the subscription server is unreachable directly"),
    "SubscriptionNeverUpdated": ("ещё не обновлялась", "not updated yet"),
    "SubscriptionUpdatedAtFormat": ("обновлена {0:g}", "updated {0:g}"),
    "SubscriptionTrafficFormat": ("трафик: {0} из {1}", "traffic: {0} of {1}"),
    "SubscriptionTrafficUsedFormat": ("трафик: {0}", "traffic: {0}"),
    "SubscriptionExpiresFormat": ("до {0:d}", "until {0:d}"),
    "SubscriptionExpired": ("срок истёк", "expired"),
    "BytesGbFormat": ("{0:0.##} ГБ", "{0:0.##} GB"),
    "BytesMbFormat": ("{0:0.#} МБ", "{0:0.#} MB"),
    "SubscriptionError_Network": ("сервер подписки недоступен", "the subscription server is unreachable"),
    "SubscriptionError_Timeout": ("сервер подписки не ответил вовремя", "the subscription server did not respond in time"),
    "SubscriptionError_HttpError": ("сервер подписки вернул ошибку", "the subscription server returned an error"),
    "SubscriptionError_TooLarge": ("ответ сервера слишком большой", "the server response is too large"),
    "SubscriptionError_NoProfiles": ("в ответе нет профилей известного формата", "the response contains no profiles in a known format"),
    # Хранилище и восстановление
    "ProxyRecoveredAfterCrash": ("Системный прокси восстановлен после аварийного завершения KHORS.", "System proxy restored after KHORS terminated unexpectedly."),
    "StorageRestoredFromBackup": ("Файл профилей был повреждён — профили восстановлены из резервной копии.", "The profiles file was damaged — profiles restored from the backup."),
    "StorageCorrupt": ("Файл профилей повреждён и отложен рядом; начат новый список.", "The profiles file is damaged and was set aside; starting a new list."),
    "StorageReadOnly": ("Профили сохранены более новой версией KHORS — изменения не записываются.", "Profiles were saved by a newer KHORS version — changes are not saved."),
    # Трей
    "TrayShow": ("Показать окно", "Show window"),
    "TrayExit": ("Выход", "Exit"),
    "TrayTooltipFormat": ("KHORS — {0}", "KHORS — {0}"),
    # Ошибки разбора ссылок (LinkParseErrorCode)
    "LinkError_Empty": ("пустая строка", "empty line"),
    "LinkError_UnsupportedScheme": ("неподдерживаемый тип ссылки", "unsupported link type"),
    "LinkError_Malformed": ("ссылка повреждена", "the link is malformed"),
    "LinkError_MissingCredentials": ("нет UUID, пароля или метода шифрования", "missing UUID, password or cipher"),
    "LinkError_MissingHost": ("нет адреса сервера", "missing server address"),
    "LinkError_InvalidPort": ("неверный порт", "invalid port"),
    "LinkError_InvalidBase64": ("повреждённые данные base64", "damaged base64 data"),
    "LinkError_InvalidJson": ("повреждённые данные JSON", "damaged JSON data"),
    "LinkError_UnsupportedTransport": ("транспорт пока не поддерживается", "transport is not supported yet"),
    "LinkError_UnsupportedSecurity": ("тип шифрования не поддерживается", "security type is not supported"),
    # Проблемы профиля (ProfileIssueCode)
    "Issue_NameEmpty": ("не задано имя", "name is empty"),
    "Issue_HostEmpty": ("не задан адрес сервера", "server address is empty"),
    "Issue_HostInvalid": ("неверный адрес сервера", "invalid server address"),
    "Issue_PortOutOfRange": ("порт вне диапазона 1–65535", "port is outside 1–65535"),
    "Issue_IdEmpty": ("не задан UUID", "UUID is empty"),
    "Issue_IdInvalid": ("неверный UUID", "invalid UUID"),
    "Issue_AlterIdNegative": ("alterId меньше нуля", "alterId is negative"),
    "Issue_PasswordEmpty": ("не задан пароль", "password is empty"),
    "Issue_MethodEmpty": ("не задан метод шифрования", "cipher is empty"),
    "Issue_FlowUnknown": ("неизвестный flow", "unknown flow"),
    "Issue_FlowRequiresTcp": ("flow работает только с TCP", "flow requires TCP"),
    "Issue_FlowRequiresTlsOrReality": ("flow требует TLS или REALITY", "flow requires TLS or REALITY"),
    "Issue_MuxIncompatibleWithFlow": ("mux несовместим с flow", "mux cannot be used with flow"),
    "Issue_MuxConcurrencyOutOfRange": ("неверное число соединений mux", "invalid mux concurrency"),
    "Issue_TransportModeUnknown": ("неизвестный режим транспорта", "unknown transport mode"),
    "Issue_XhttpExtraInvalid": ("параметр extra XHTTP — не JSON-объект", "XHTTP extra is not a JSON object"),
    "Issue_RealitySniEmpty": ("REALITY: не задан SNI", "REALITY: SNI is empty"),
    "Issue_RealityPublicKeyInvalid": ("REALITY: неверный публичный ключ", "REALITY: invalid public key"),
    "Issue_RealityShortIdInvalid": ("REALITY: неверный short id", "REALITY: invalid short id"),
    "Issue_RealityTransportUnsupported": ("REALITY не работает с этим транспортом", "REALITY does not work with this transport"),
    "Issue_RealityMlDsa65VerifyInvalid": ("REALITY: неверный ключ pqv", "REALITY: invalid pqv key"),
    "Issue_RealityPostQuantumRequiresXray": ("сервер требует X25519MLKEM768 — нужно ядро Xray, sing-box не подключится", "the server requires X25519MLKEM768 — use the Xray core, sing-box cannot connect"),
    "Issue_TlsPinnedCertInvalid": ("неверный отпечаток сертификата (pcs)", "invalid certificate fingerprint (pcs)"),
    "Issue_QuicRequiresTls": ("Hysteria2 и TUIC работают только с TLS", "Hysteria2 and TUIC require TLS"),
    "Issue_ObfsInvalid": ("обфускация: нужен salamander с паролем", "obfuscation: salamander with a password is required"),
    "Issue_PortsInvalid": ("неверный список портов", "invalid port list"),
    "Issue_TuicModeUnknown": ("TUIC: неизвестный congestion control или режим UDP", "TUIC: unknown congestion control or UDP mode"),
    "Issue_WireGuardKeyInvalid": ("WireGuard: неверный ключ (нужно 32 байта в base64)", "WireGuard: invalid key (32 bytes in base64 expected)"),
    "Issue_WireGuardAddressInvalid": ("WireGuard: неверный адрес интерфейса (нужно вида 10.0.0.2/32)", "WireGuard: invalid interface address (like 10.0.0.2/32)"),
    "Issue_WireGuardReservedInvalid": ("WireGuard: reserved — три числа 0–255", "WireGuard: reserved must be three numbers 0–255"),
    "Issue_MtuOutOfRange": ("MTU вне диапазона 576–65535", "MTU outside 576–65535"),
    "Issue_InsecureTls": ("проверка сертификата отключена (allowInsecure)", "certificate verification is disabled (allowInsecure)"),
    "Issue_UnknownParameters": ("в ссылке есть параметры, которые KHORS пока не понимает", "the link has parameters KHORS does not understand yet"),
    # Причины неудачи подключения (ConnectionFailureKind)
    "Failure_ProfileInvalid": ("В профиле ошибка: {0}.", "The profile has an error: {0}."),
    "Failure_UnsupportedByCore": ("Ядро {0} не поддерживает этот профиль: {1}.", "The {0} core does not support this profile: {1}."),
    "Failure_CoreNotFound": ("Не найден исполняемый файл ядра {0}. Переустановите KHORS.", "The {0} core executable was not found. Reinstall KHORS."),
    "Failure_CoreStartFailed": ("Ядро {0} не запустилось (код {1}). Подробности — в логе ниже.", "The {0} core failed to start (code {1}). See the log below."),
    "Failure_CoreCrashed": ("Ядро {0} неожиданно завершилось (код {1}). Системный прокси возвращён.", "The {0} core exited unexpectedly (code {1}). System proxy restored."),
    "Failure_SystemProxyFailed": ("Не удалось включить системный прокси Windows.", "Could not enable the Windows system proxy."),
    # Что именно не поддерживает Xray (CoreConfigError.Field, точки заменены на _)
    "Unsupported_security_allowInsecure": ("отключённая проверка сертификата (allowInsecure) удалена в Xray 26",
                                           "disabled certificate verification (allowInsecure) was removed in Xray 26"),
    "Unsupported_security": ("VLESS/Trojan без TLS к публичному адресу", "VLESS/Trojan without TLS to a public address"),
    "Unsupported_protocol_plugin": ("плагины Shadowsocks", "Shadowsocks plugins"),
    "Unsupported_protocol_alterId": ("устаревший VMess (alterId больше 0)", "legacy VMess (alterId greater than 0)"),
    "Unsupported_protocol": ("Hysteria2, TUIC и WireGuard работают только через sing-box", "Hysteria2, TUIC and WireGuard work only through sing-box"),
    "Unsupported_protocol_encryption": ("VLESS Encryption есть только в Xray", "VLESS Encryption is available only in Xray"),
    "Unsupported_transport": ("транспорт XHTTP есть только в Xray", "the XHTTP transport is available only in Xray"),
    "Unsupported_transport_headerType": ("маскировка TCP под HTTP есть только в Xray", "TCP HTTP header obfuscation is available only in Xray"),
    "Unsupported_transport_mode": ("gRPC в режиме multi есть только в Xray", "gRPC multi mode is available only in Xray"),
    "Unsupported_security_pinnedPeerCertSha256": ("закреплённый сертификат (pinSHA256): sing-box закрепляет ключ, а не сертификат — используйте сертификат от доверенного центра (например, Let's Encrypt)", "pinned certificate (pinSHA256): sing-box pins the public key, not the certificate — use a certificate from a trusted CA (e.g. Let's Encrypt)"),
    "Unsupported_security_verifyPeerCertByName": ("проверка сертификата по имени (vcn) есть только в Xray", "certificate verification by name (vcn) is available only in Xray"),
    "Unsupported_security_supportsX25519MlKem768": ("сервер ждёт X25519MLKEM768, а sing-box его не отправляет — выберите ядро Xray", "the server expects X25519MLKEM768, which sing-box does not send — use the Xray core"),
    "Unsupported_security_mlDsa65Verify": ("ключ ML-DSA-65 (pqv) есть только в Xray", "the ML-DSA-65 key (pqv) is available only in Xray"),
    "Unsupported_core": ("профилю вручную назначено другое ядро", "the profile is manually assigned to another core"),
    "Unsupported_Other": ("неподдерживаемая возможность", "an unsupported feature"),
}

HEADER = """<?xml version="1.0" encoding="utf-8"?>
<!-- Сгенерировано tools/i18n/strings.py — правьте таблицу там. -->
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
"""


def write(path, index):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER)
        for key in sorted(STRINGS):
            f.write(f'  <data name="{key}" xml:space="preserve"><value>{escape(STRINGS[key][index])}</value></data>\n')
        f.write("</root>\n")


if __name__ == "__main__":
    base = os.path.join(os.path.dirname(__file__), "..", "..", "src", "Khors.App", "Resources")
    write(os.path.join(base, "Strings.resx"), 1)
    write(os.path.join(base, "Strings.ru.resx"), 0)
    print(f"{len(STRINGS)} строк")
