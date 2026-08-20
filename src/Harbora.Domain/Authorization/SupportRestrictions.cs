namespace Harbora.Domain.Authorization;

/// <summary>
/// The acts a platform administrator may not perform while signed in as a customer.
///
/// <para>
/// The list is short on purpose. Support usually has to <i>do</i> the thing to see it fail, so
/// everything not named here stays allowed — deploying, restarting, editing an env var, breaking a
/// route. What is refused is the small set that either takes the account away from its owner or
/// moves money, because neither is something a customer could reasonably discover afterwards from
/// the outcome alone.
/// </para>
///
/// <para>
/// Each refusal is a sentence rather than a boolean for the reason
/// <see cref="UserAdministration"/> gives: somebody is about to be told no, and "forbidden" does not
/// tell them which rule they hit. The English and Persian are both here because the panel renders
/// Persian by default and a refusal in the wrong language is a refusal nobody reads.
/// </para>
/// </summary>
public static class SupportRestrictions
{
    /// <summary>Why this act is refused, in the reader's own language.</summary>
    public static string Refusal(SupportRestrictedAct act, bool isFa) => act switch
    {
        SupportRestrictedAct.Password => isFa
            ? "در نشست پشتیبانی نمی‌توان رمز حساب را عوض کرد."
            : "A support session cannot change an account's password.",
        SupportRestrictedAct.Email => isFa
            ? "در نشست پشتیبانی نمی‌توان نشانی ایمیل حساب را تغییر داد یا تأیید کرد."
            : "A support session cannot change or verify an account's email address.",
        SupportRestrictedAct.TwoFactor => isFa
            ? "در نشست پشتیبانی نمی‌توان ورود دومرحله‌ای را روشن، خاموش یا بازنشانی کرد."
            : "A support session cannot turn two-factor on, off, or reset it.",
        SupportRestrictedAct.Sessions => isFa
            ? "در نشست پشتیبانی نمی‌توان نشست‌های دیگر این حساب را بست."
            : "A support session cannot end this account's other sessions.",
        SupportRestrictedAct.ApiToken => isFa
            ? "در نشست پشتیبانی نمی‌توان توکن API ساخت."
            : "A support session cannot create an API token.",
        SupportRestrictedAct.WalletCredit => isFa
            ? "در نشست پشتیبانی نمی‌توان کیف پول را شارژ یا اصلاح کرد."
            : "A support session cannot credit or adjust a wallet.",
        SupportRestrictedAct.ExternalLogin => isFa
            ? "در نشست پشتیبانی نمی‌توان حساب ورود خارجی (گوگل، گیت‌هاب، OIDC) را به این حساب وصل یا از آن جدا کرد."
            : "A support session cannot connect or disconnect an external sign-in (Google, GitHub, OIDC).",
        _ => isFa ? "این کار در نشست پشتیبانی انجام نمی‌شود." : "A support session cannot do this."
    };

    /// <summary>
    /// The audit action written when one of these is refused. Prefixed <c>support.</c> like every
    /// other row a support session writes, so the customer's own page finds it with one filter.
    /// </summary>
    public const string RefusedAction = "support.refused";
}

/// <summary>
/// What was refused. Named acts rather than route strings: the same act is reachable from more than
/// one action (two-factor from the account page and from user administration), and a customer
/// reading their support-access page needs to know what was attempted, not which URL it was on.
/// </summary>
public enum SupportRestrictedAct
{
    Password = 0,
    Email = 1,
    TwoFactor = 2,
    Sessions = 3,
    ApiToken = 4,
    WalletCredit = 5,

    /// <summary>
    /// Connecting or disconnecting a Google/GitHub/OIDC identity.
    ///
    /// <para>
    /// Not on the plan's original list, because external sign-in did not exist when that list was
    /// written — it landed from a parallel sub-project while this one was in flight. It belongs
    /// here by the same rule everything else on the list is here by: linking an identity mints a
    /// durable, self-owned way into somebody else's account, which is what an API token does and
    /// worse, and unlinking takes one of the customer's own ways in away. Reported rather than
    /// slipped in — see the sub-project report.
    /// </para>
    /// </summary>
    ExternalLogin = 6
}
