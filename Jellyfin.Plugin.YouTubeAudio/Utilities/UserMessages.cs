using System;
using System.IO;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.YouTubeAudio.Utilities;

/// <summary>
/// Builds the exceptions whose message a page shows, keyed to a translation, and reads them back in the viewer's language.
/// The message of each exception is the US English, for the log and the tests.
/// </summary>
internal static class UserMessages
{
    private const string KeyName = "YouTubeAudio.TranslationKey";
    private const string ArgsName = "YouTubeAudio.TranslationArgs";

    /// <summary>
    /// Builds an <see cref="ArgumentException"/> for input a person can fix.
    /// </summary>
    /// <param name="key">The translation key, from <c>TranslationKeys</c>.</param>
    /// <param name="args">The values for the string's placeholders.</param>
    /// <returns>The exception.</returns>
    internal static ArgumentException Argument(string key, params object?[] args)
        => Mark(new ArgumentException(BaseTranslations.Format(key, null, args)), key, args);

    /// <summary>
    /// Builds an <see cref="ArgumentException"/> for input a person can fix, caused by another exception.
    /// </summary>
    /// <param name="key">The translation key, from <c>TranslationKeys</c>.</param>
    /// <param name="inner">The exception that caused it.</param>
    /// <returns>The exception.</returns>
    internal static ArgumentException Argument(string key, Exception inner)
        => Mark(new ArgumentException(BaseTranslations.Get(key), inner), key, []);

    /// <summary>
    /// Builds a <see cref="FileNotFoundException"/> for a song or file that is gone.
    /// </summary>
    /// <param name="key">The translation key, from <c>TranslationKeys</c>.</param>
    /// <returns>The exception.</returns>
    internal static FileNotFoundException NotFound(string key)
        => Mark(new FileNotFoundException(BaseTranslations.Get(key)), key, []);

    /// <summary>
    /// Builds an <see cref="InvalidOperationException"/> for an action the plugin's state refuses.
    /// </summary>
    /// <param name="key">The translation key, from <c>TranslationKeys</c>.</param>
    /// <param name="args">The values for the string's placeholders.</param>
    /// <returns>The exception.</returns>
    internal static InvalidOperationException Conflict(string key, params object?[] args)
        => Mark(new InvalidOperationException(BaseTranslations.Format(key, null, args)), key, args);

    /// <summary>
    /// Builds an <see cref="InvalidOperationException"/> for an action the plugin's state refuses, caused by another exception.
    /// </summary>
    /// <param name="key">The translation key, from <c>TranslationKeys</c>.</param>
    /// <param name="inner">The exception that caused it.</param>
    /// <param name="args">The values for the string's placeholders.</param>
    /// <returns>The exception.</returns>
    internal static InvalidOperationException Conflict(string key, Exception inner, params object?[] args)
        => Mark(new InvalidOperationException(BaseTranslations.Format(key, null, args), inner), key, args);

    /// <summary>
    /// Gets the text a page shows for an exception, in the viewer's language when it carries a translation key.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <param name="language">The viewer's language, or <c>null</c> for US English.</param>
    /// <returns>The translated text, or the exception's own message when it has no key.</returns>
    internal static string Text(Exception exception, string? language)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Data[KeyName] is string key
            ? BaseTranslations.Format(key, language, exception.Data[ArgsName] as object?[] ?? [])
            : exception.Message;
    }

    /// <summary>
    /// Builds the error body a page shows for an exception, in the viewer's language when it carries a translation key.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <param name="request">The request, read for the page's language with <see cref="BaseTranslations.RequestLanguage"/>.</param>
    /// <returns>An object with an Error message.</returns>
    internal static object ErrorBody(Exception exception, HttpRequest request) => new { Error = Text(exception, BaseTranslations.RequestLanguage(request)) };

    /// <summary>
    /// Builds the error body a page shows for one translated message, in the viewer's language.
    /// </summary>
    /// <param name="key">The translation key, from <c>TranslationKeys</c>.</param>
    /// <param name="request">The request, read for the page's language with <see cref="BaseTranslations.RequestLanguage"/>.</param>
    /// <returns>An object with an Error message.</returns>
    internal static object MessageBody(string key, HttpRequest request) => new { Error = BaseTranslations.Get(key, BaseTranslations.RequestLanguage(request)) };

    /// <summary>
    /// Marks an exception of any type with a translation key, so a page shows it in the viewer's language.
    /// </summary>
    /// <typeparam name="T">The exception type.</typeparam>
    /// <param name="exception">The exception, whose message is the key's US English.</param>
    /// <param name="key">The translation key, from <c>TranslationKeys</c>.</param>
    /// <param name="args">The values for the string's placeholders.</param>
    /// <returns>The same exception.</returns>
    internal static T Mark<T>(T exception, string key, params object?[] args)
        where T : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        exception.Data[KeyName] = key;
        exception.Data[ArgsName] = args;
        return exception;
    }
}
