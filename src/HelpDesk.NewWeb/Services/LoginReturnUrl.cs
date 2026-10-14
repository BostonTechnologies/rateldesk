namespace HelpDesk.NewWeb.Services;

/// <summary>Carry an application path through sign-in, without permitting an external redirect.</summary>
public static class LoginReturnUrl
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048) return "/home";
        var decoded = Uri.UnescapeDataString(value);
        if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal) ||
            !decoded.StartsWith('/') || decoded.StartsWith("//", StringComparison.Ordinal) ||
            value.Contains('\\') || decoded.Contains('\\') || value.Any(char.IsControl) || decoded.Any(char.IsControl))
            return "/home";
        return value;
    }
    public static string WithReturn(string destination, string? returnUrl)
    {
        var path = Normalize(returnUrl);
        return path == "/home" ? destination : destination + (destination.Contains('?') ? "&" : "?") +
            "returnUrl=" + Uri.EscapeDataString(path);
    }
}
