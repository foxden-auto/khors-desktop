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
    "LocalProxyFormat": ("Локальный прокси: HTTP 127.0.0.1:{0}, SOCKS5 127.0.0.1:{1}", "Local proxy: HTTP 127.0.0.1:{0}, SOCKS5 127.0.0.1:{1}"),
    "FailureDetails": ("Лог ядра", "Core log"),
    # Профили
    "ProfilesHeader": ("Профили", "Profiles"),
    "ButtonImportClipboard": ("Вставить из буфера", "Paste from clipboard"),
    "ButtonDelete": ("Удалить", "Delete"),
    "EmptyProfilesHint": ("Скопируйте ссылку vless://, vmess://, trojan:// или ss:// и нажмите «Вставить из буфера».",
                          "Copy a vless://, vmess://, trojan:// or ss:// link and press “Paste from clipboard”."),
    "ImportResultFormat": ("Добавлено: {0}. Уже были: {1}. Не распознано: {2}.", "Added: {0}. Already present: {1}. Not recognized: {2}."),
    "ImportLineErrorFormat": ("Ссылка {0}: {1}", "Link {0}: {1}"),
    "ClipboardEmpty": ("В буфере обмена нет текста.", "The clipboard contains no text."),
    "ProfileWarningsFormat": ("Предупреждение: {0}", "Warning: {0}"),
    "ProfileUnknownParamsFormat": ("Параметры ссылки, которые KHORS пока не понимает: {0}", "Link parameters KHORS does not understand yet: {0}"),
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
    "Issue_InsecureTls": ("проверка сертификата отключена (allowInsecure)", "certificate verification is disabled (allowInsecure)"),
    "Issue_UnknownParameters": ("в ссылке есть параметры, которые KHORS пока не понимает", "the link has parameters KHORS does not understand yet"),
    # Причины неудачи подключения (ConnectionFailureKind)
    "Failure_ProfileInvalid": ("В профиле ошибка: {0}.", "The profile has an error: {0}."),
    "Failure_UnsupportedByCore": ("Xray не поддерживает этот профиль: {0}.", "Xray does not support this profile: {0}."),
    "Failure_CoreNotFound": ("Не найден исполняемый файл Xray. Переустановите KHORS.", "The Xray executable was not found. Reinstall KHORS."),
    "Failure_CoreStartFailed": ("Xray не запустился (код {0}). Подробности — в логе ниже.", "Xray failed to start (code {0}). See the log below."),
    "Failure_CoreCrashed": ("Xray неожиданно завершился (код {0}). Системный прокси возвращён.", "Xray exited unexpectedly (code {0}). System proxy restored."),
    "Failure_SystemProxyFailed": ("Не удалось включить системный прокси Windows.", "Could not enable the Windows system proxy."),
    # Что именно не поддерживает Xray (CoreConfigError.Field, точки заменены на _)
    "Unsupported_security_allowInsecure": ("отключённая проверка сертификата (allowInsecure) удалена в Xray 26",
                                           "disabled certificate verification (allowInsecure) was removed in Xray 26"),
    "Unsupported_security": ("VLESS/Trojan без TLS к публичному адресу", "VLESS/Trojan without TLS to a public address"),
    "Unsupported_protocol_plugin": ("плагины Shadowsocks", "Shadowsocks plugins"),
    "Unsupported_protocol_alterId": ("устаревший VMess (alterId больше 0)", "legacy VMess (alterId greater than 0)"),
    "Unsupported_core": ("профилю назначено ядро sing-box", "the profile is assigned to sing-box"),
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
