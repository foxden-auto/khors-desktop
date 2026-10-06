namespace Khors.Platform;

/// <summary>
/// Системные настройки DNS: применение и откат. В режиме TUN DNS перехватывает sing-box (ROADMAP 3.4), поэтому методы
/// появятся вместе с Kill Switch (ROADMAP 4.1), если понадобятся.
/// </summary>
public interface IDnsConfigurator;
