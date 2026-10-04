namespace Khors.Core.Diagnostics;

/// <summary>Вид маскируемого значения; определяет метку в маске, например <c>{uuid-1a2b3c}</c>.</summary>
public enum SecretKind
{
    /// <summary>UUID / id пользователя (VLESS, VMess, TUIC).</summary>
    Uuid,

    /// <summary>Пароль, auth, имя пользователя прокси.</summary>
    Password,

    /// <summary>Ключи: REALITY public/private key, WireGuard, pre-shared key.</summary>
    Key,

    /// <summary>REALITY short id.</summary>
    ShortId,

    /// <summary>Адрес сервера: домен или IP.</summary>
    Host,

    /// <summary>Токен (например, в URL подписки).</summary>
    Token,
}
