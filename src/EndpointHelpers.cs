using Microsoft.AspNetCore.Http;
using System;

internal static class EndpointHelpers
{
    public static void SetCookie(HttpResponse res, string name, string value, bool isHttps, TimeSpan maxAge) =>
        res.Cookies.Append(name, value, new CookieOptions
        {
            HttpOnly = true,    // mitigate XSS
            Secure = isHttps,   // must be true over HTTPS
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = maxAge
        });

    public static void DeleteCookie(HttpResponse res, string name) =>
        res.Cookies.Delete(name, new CookieOptions { Path = "/" });
}
