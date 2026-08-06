"""Fills the Persian resource file from the keys the views actually ask for.

The panel injects one shared localiser in _ViewImports, so every `T["…"]` in every view resolves
against SharedResource.fa.resx. A key with no entry there falls back to the key itself — which is
English — so a missing translation is not a missing string, it is an English sentence in the middle
of a Persian page. About a hundred of them had accumulated: the whole landing page, every error
page, the audit log and the rollback confirmation.

Keys are matched by a distinctive prefix rather than by the whole sentence, because several contain
an em dash that does not survive a round trip through a console.

Run:  python scripts/add-persian-strings.py
"""
import json
import pathlib
import re
import sys
from xml.sax.saxutils import escape

ROOT = pathlib.Path(__file__).resolve().parents[1]
RESX = ROOT / "src" / "Harbora.Web" / "Resources" / "SharedResource.fa.resx"
VIEWS = ROOT / "src" / "Harbora.Web" / "Views"

KEY = re.compile(r'T\["([^"]*)"\]')
DATA = re.compile(r'<data name="([^"]*)"')

# prefix -> Persian. The prefix must be long enough to be unique among the missing keys.
TRANSLATIONS = {
    # --- audit log ---
    "Audit log": "گزارش رخدادها",
    "Privileged actions across the platform": "کارهای حساس در سراسر پلتفرم، تازه‌ترین بالا.",
    "Export CSV": "خروجی CSV",
    "All actions": "همهٔ کارها",
    "Actor email": "ایمیل شخص",
    "Actor": "شخص",
    "Action": "کار",
    "Filter": "فیلتر",
    "No audit entries match this filter.": "هیچ رخدادی با این فیلتر پیدا نشد.",
    "When": "زمان",
    "IP": "IP",
    "Page": "صفحه",
    "entries": "رخداد",
    "Previous": "قبلی",
    # --- error pages ---
    "Page not found": "صفحه پیدا نشد",
    "This address doesn't exist in Harbora":
        "این آدرس در Harbora وجود ندارد. ممکن است تغییر نام داده باشد، یا اپ یا استقراری که به آن"
        " اشاره می‌کرد حذف شده باشد.",
    "You don't have access to this": "به این بخش دسترسی ندارید",
    "Your role doesn't include this action":
        "نقش شما این کار را شامل نمی‌شود. از مالک یا مدیر این فضای کاری بخواهید دسترسی بدهد.",
    "Please sign in": "لطفاً وارد شوید",
    "Your session has ended": "نشست شما تمام شده است. برای ادامه دوباره وارد شوید.",
    "Too many requests": "درخواست بیش از حد",
    "You've hit the rate limit":
        "به سقف تعداد این درخواست رسیده‌اید. یک دقیقه صبر کنید و دوباره امتحان کنید.",
    "That request couldn't be completed": "این درخواست انجام نشد",
    "Harbora couldn't handle that request.": "Harbora نتوانست این درخواست را انجام دهد.",
    "Something went wrong": "چیزی درست پیش نرفت",
    "Harbora hit an unexpected error":
        "Harbora به خطای پیش‌بینی‌نشده خورد. جزئیاتش در لاگ‌های پنل نوشته شد.",
    "Harbora is starting up": "Harbora در حال بالا آمدن است",
    "A required service isn't ready yet":
        "یکی از سرویس‌های لازم هنوز آماده نیست. معمولاً تا یک دقیقه پس از راه‌اندازی مجدد درست می‌شود.",
    "Go back": "برگشت",
    "Back to dashboard": "برگشت به داشبورد",
    "Requested": "درخواست‌شده",
    "Reference": "شناسه",
    "On the server, run": "روی سرور اجرا کنید",
    "to check configuration and container health.": "تا تنظیمات و سلامت کانتینرها بررسی شود.",
    # --- landing page ---
    "Deploy without the ceremony": "استقرار، بدون تشریفات",
    "Deploy from Git, a Dockerfile":
        "از Git، از Dockerfile، از ایمیج آماده یا از سایت استاتیک مستقر کنید. Harbora می‌سازدش، با"
        " SSL خودکار دامنه می‌دهد، سلامتش را می‌سنجد و تنها وقتی ترافیک را عوض می‌کند که نسخهٔ تازه"
        " واقعاً بالا آمده باشد.",
    "Get started": "شروع کنید",
    "See how it works": "ببینید چطور کار می‌کند",
    "Everything a deploy needs": "هرچه یک استقرار لازم دارد",
    "Not a thin wrapper over docker run":
        "یک پوستهٔ نازک روی docker run نیست — همان بخش‌هایی که درست‌کردنشان خسته‌کننده است، همان‌هایی"
        " هستند که Harbora خودش برعهده می‌گیرد.",
    "Three steps to production": "سه قدم تا محصول",
    "From an empty server to a running app":
        "از یک سرور خالی تا اپی که اجرا می‌شود و گواهی معتبر دارد.",
    "Push your code.": "کدتان را push کنید.",
    "We handle the rest.": "بقیه‌اش با ما.",
    "Ready to deploy?": "آمادهٔ استقرار؟",
    "Sign in to the panel and put your first app live":
        "وارد پنل شوید و اولین اپتان را در حدود یک دقیقه بالا بیاورید.",
    "Questions": "پرسش‌ها",
    "No plans have been published yet.": "هنوز پلنی منتشر نشده است.",
    "Quotas are enforced by the platform, not by trust.":
        "سهمیه‌ها را خود پلتفرم اعمال می‌کند، نه اعتماد.",
    "Automatic SSL & backups": "SSL و پشتیبان خودکار",
    "SSL certificates": "گواهی‌های SSL",
    "Self-hosted PaaS": "PaaS خودمیزبان · بومیِ Docker",
    "Popular": "پرطرفدار",
    "Unlimited apps": "اپ نامحدود",
    "Unlimited databases": "دیتابیس نامحدود",
    "Unlimited": "نامحدود",
    "instant rollback": "بازگردانی آنی",
    "downtime cutover": "قطعی هنگام جابه‌جایی",
    "databases": "دیتابیس",
    "memory": "حافظه",
    "month": "ماه",
    "click": "کلیک",
    "disk": "دیسک",
    "apps": "اپ",
    "Auto": "خودکار",
    "Free": "رایگان",
    "Zero": "صفر",
    # --- landing shell ---
    "Your own deployment platform": "پلتفرم استقرار خودتان — روی سرورهای خودتان.",
    "Deploy any app from Git in seconds":
        "هر اپی را در چند ثانیه از Git مستقر کنید — با SSL، دیتابیس، پشتیبان و پایش خودکار. پلتفرم"
        " خودتان، روی سرورهای خودتان.",
    "Built with Docker, Traefik and .NET": "ساخته‌شده با Docker، Traefik و ‎.NET",
    "How it works": "چطور کار می‌کند",
    "Documentation": "مستندات",
    "Resources": "منابع",
    "Features": "امکانات",
    "Product": "محصول",
    "Account": "حساب",
    "FAQ": "پرسش‌های پرتکرار",
    # --- rollback confirmation ---
    "This rollback isn't possible": "این بازگردانی ممکن نیست",
    "You are about to restore": "در حال بازگرداندن",
    "Rolling back to": "بازگردانی به",
    "Roll back to": "بازگردانی به نسخهٔ",
    "Roll back": "بازگردانی",
    "This image is already built":
        "این ایمیج از قبل ساخته شده، پس چیزی دوباره build نمی‌شود. کانتینر تازه کنار کانتینر فعلی"
        " بالا می‌آید و ترافیک تنها پس از قبولی در بررسی سلامت جابه‌جا می‌شود.",
    "Currently live": "الان روی هواست",
    "Deployed": "مستقرشده",
    "Commit": "کامیت",
    "Author": "نویسنده",
    "Image": "ایمیج",
    "Back": "برگشت",
    # --- odds and ends the shared file never had ---
    "Scheduled runs": "اجراهای زمان‌بندی‌شده",
    "This job has not run yet.": "این کار هنوز اجرا نشده است.",
    "Could not run": "اجرا نشد",
    "run by hand": "اجرای دستی",
    "Run now": "همین حالا اجرا کن",
    "Platform Name": "نام پلتفرم",
    "Delete": "حذف",
    "Exit": "خروج",
    "Next": "بعدی",
    "Theme": "پوسته",
    "System": "سیستم",
    "Light": "روشن",
    "Dark": "تیره",
}


def used_keys():
    keys = set()
    for view in VIEWS.rglob("*.cshtml"):
        keys |= set(KEY.findall(view.read_text(encoding="utf-8")))
    return keys


def translate(key: str) -> str | None:
    """Longest matching prefix wins, so "Actor email" is not answered by "Actor"."""
    best = None
    for prefix, persian in TRANSLATIONS.items():
        if key.startswith(prefix) and (best is None or len(prefix) > len(best[0])):
            best = (prefix, persian)
    return best[1] if best else None


def main():
    text = RESX.read_text(encoding="utf-8")
    have = set(DATA.findall(text))
    missing = sorted(k for k in used_keys() if k not in have)

    rows, unmatched = [], []
    for key in missing:
        persian = translate(key)
        if persian is None:
            unmatched.append(key)
            continue
        rows.append(f'  <data name="{escape(key, {chr(34): "&quot;"})}">'
                    f'<value>{escape(persian)}</value></data>')

    if rows:
        RESX.write_text(text.replace("</root>", "\n".join(rows) + "\n</root>"), encoding="utf-8")

    print(f"added {len(rows)} translation(s)")
    for key in unmatched:
        print("  NO TRANSLATION:", key[:80])
    return 1 if unmatched else 0


if __name__ == "__main__":
    sys.exit(main())
