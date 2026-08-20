namespace Harbora.Infrastructure.Services;

/// <summary>
/// The decision behind the self-serve "import a dump" confirm button — sub-project 10.
///
/// <para>
/// Same idiom as <see cref="ServiceRemovalPlan"/> (do-not-change list item 19): a destructive act is
/// confirmed by typing the resource's own name, not a native <c>confirm()</c> and not a generic word.
/// Unlike removal, typing the name is never optional here — an import always overwrites the database's
/// current contents, so there is no "reversible" branch to skip the prompt for.
/// </para>
/// </summary>
public static class DatabaseImportPlan
{
    public static bool IsConfirmed(string? typedName, string serviceName) =>
        string.Equals(typedName?.Trim(), serviceName?.Trim(), StringComparison.Ordinal);
}
