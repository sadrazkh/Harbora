namespace Harbora.Application.Abstractions;

/// <summary>
/// Why a service will not do what it was asked, in both languages — the shape every extracted
/// operation service returns instead of writing to a controller's ModelState. The controller maps it
/// to its form; the AI agent hands <see cref="Message"/> back to the model and shows
/// <see cref="MessageFa"/> to a Persian user.
/// </summary>
/// <param name="Field">The request property it is about, when there is one (for a form's field error).</param>
public sealed record ServiceRefusal(string Message, string MessageFa, string? Field = null);

/// <summary>
/// The result of a mutating operation service call: either a value, or the refusals that stopped it.
/// Nothing was changed when <see cref="Refusals"/> is non-empty.
/// </summary>
public sealed record ServiceOutcome<T>(T? Value, IReadOnlyList<ServiceRefusal> Refusals)
{
    public bool Ok => Refusals.Count == 0;

    public static ServiceOutcome<T> Success(T value) => new(value, []);

    public static ServiceOutcome<T> Refused(params ServiceRefusal[] refusals) => new(default, refusals);

    public static ServiceOutcome<T> Refused(IReadOnlyList<ServiceRefusal> refusals) => new(default, refusals);
}
