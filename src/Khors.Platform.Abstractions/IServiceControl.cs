namespace Khors.Platform;

/// <summary>
/// Привилегированная служба: установка, удаление, запуск, статус
/// (Windows — служба KhorsService, Linux — юнит systemd). Методы — в ROADMAP 3.1.
/// </summary>
public interface IServiceControl;
