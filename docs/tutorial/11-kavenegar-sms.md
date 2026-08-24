# ۱۱ — پیامک با کاوه‌نگار

سرویس‌های بین‌المللی پیامک — Twilio، MessageBird — حساب‌های ایرانی را می‌بندند؛ همین یک خط، کاوه‌نگار
را به استاندارد پیامک/OTP در بازار ایران تبدیل کرده. Harbora **هیچ سرویس پیامکِ خودش** نمی‌سازد — نه
انتزاعی برای «SMS»، نه جدول ارائه‌دهنده، نه ذخیرهٔ اعتبارنامه‌ای فراتر از یک متغیر محیطی ساده. این
فصل یک قصهٔ یکپارچه‌سازی است: چطور از اپ خودتان پیامک بفرستید، و چطور از کاوه‌نگار وضعیت تحویل را
پس بگیرید.

> این فصل، مثل فصل ۱۰، تصویر ندارد — همان دلیل: این جلسه به پنل در حال اجرا یا Docker دسترسی
> نداشت. مسیرها و برچسب‌ها از روی سورس پنل نوشته شده‌اند.

## قالب چه می‌سازد

**قالب‌ها** › **Kavenegar SMS Starter** یک اپ وب مینیمال Node.js می‌سازد (نوع سرویس آن پیش‌فرض،
یعنی **Web** — برخلاف قالب ربات تلگرام فصل قبل، این یکی واقعاً HTTP سرو می‌کند). یک نقطهٔ پایانی
`POST /send-otp` دارد که مستقیم با REST API کاوه‌نگار حرف می‌زند — بدون هیچ SDK ای.

## قدم ۱ — گرفتن کلید API

۱. در [پنل کاوه‌نگار](https://panel.kavenegar.com) وارد شوید.
۲. بخش **وب‌سرویس** (API) را باز کنید و **API-KEY** حساب‌تان را کپی کنید.

## قدم ۲ — استقرار قالب

۱. **قالب‌ها** › دنبال «Kavenegar» بگردید › **Kavenegar SMS Starter** › **استقرار**.
۲. **نام پروژه** و **نام برنامه** را بدهید.
۳. بخش **منبع کد**: آدرس مخزن Git خودتان (کد قدم ۳) را بدهید.
۴. بخش **پیکربندی لازم**: فیلد `KAVENEGAR_API_KEY` را می‌بینید — رمز و نقطه‌چین، دقیقاً مثل توکن
   ربات تلگرام فصل قبل. کلیدی که از پنل کاوه‌نگار گرفتید را همین‌جا بچسبانید. این مقدار روی همان
   مسیری که رمز دیتابیس رمزنگاری می‌شود، رمزنگاری می‌شود — نه یک مقدار ساختگی، چون Harbora نمی‌تواند
   کلید یک حساب کاوه‌نگار را حدس بزند.
۵. **ساخت و استقرار** را بزنید. این اپ — برخلاف ربات فصل قبل — یک دامنه هم می‌گیرد، چون HTTP سرو
   می‌کند.

## قدم ۳ — ارسال OTP، بدون SDK

```json
// package.json
{
  "name": "harbora-kavenegar-sms-starter",
  "version": "1.0.0",
  "type": "module",
  "engines": { "node": ">=18" },
  "scripts": { "start": "node index.js" }
}
```

```js
// index.js
import { createServer } from "node:http";

const apiKey = process.env.KAVENEGAR_API_KEY;
if (!apiKey) throw new Error("KAVENEGAR_API_KEY is not set");

const port = process.env.PORT || 3000;

// https://kavenegar.com/rest.html — the plain REST endpoint, no SDK. "sender" is optional;
// left out here, Kavenegar sends from the account's own default line.
async function sendSms(receptor, message) {
  const url = new URL(`https://api.kavenegar.com/v1/${apiKey}/sms/send.json`);
  url.searchParams.set("receptor", receptor);
  url.searchParams.set("message", message);

  const res = await fetch(url);
  return res.json();
}

const server = createServer(async (req, res) => {
  if (req.method === "GET" && req.url === "/") {
    res.writeHead(200);
    res.end("ok");
    return;
  }

  if (req.method === "POST" && req.url === "/send-otp") {
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const body = JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}");
    const { receptor, code } = body;

    if (!receptor || !code) {
      res.writeHead(400, { "content-type": "application/json" });
      res.end(JSON.stringify({ error: "receptor and code are required" }));
      return;
    }

    const kavenegar = await sendSms(receptor, `کد ورود شما: ${code}`);
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify(kavenegar));
    return;
  }

  res.writeHead(404);
  res.end();
});

server.listen(port, () => console.log(`listening on ${port}`));
```

`curl` یک تست ساده:

```bash
curl -X POST https://YOUR-APP-DOMAIN/send-otp \
  -H "content-type: application/json" \
  -d '{"receptor":"09120000000","code":"482913"}'
```

**جایگزین با SDK رسمی.** بستهٔ رسمی `kavenegar` روی npm هست (`npm i kavenegar`، امضای callback-محور
و چند سالی هم به‌روزرسانی نشده)؛ برای یک starter تازه، تماس مستقیم REST بالا هم کمتر است و هم
به‌روزتر. اگر پروژه‌تان از قبل با آن SDK کار می‌کند، همان را نگه دارید — فقط کلید را باز هم از همین
متغیر محیطی بخوانید.

## قدم ۴ — گرفتن وضعیت تحویل، با یک فانکشن عمومی

کاوه‌نگار دو راه برای وضعیت تحویل دارد:

* **استعلام دستی** — `GET /v1/{کلید}/sms/status.json?messageid=...` — فقط برای پیامک‌های ۴۸ ساعت
  اخیر جواب می‌دهد.
* **Status Callback URL** — از پنل کاوه‌نگار، تنظیمات همان خط، یک آدرس بدهید تا کاوه‌نگار خودش با
  هر تغییر وضعیت، یک درخواست به آن آدرس بزند.

راه دوم دقیقاً همان چیزی است که **فانکشن عمومی** Harbora برایش ساخته شده — یک آدرس HTTPS که چیزی
غیر از دریافت و ثبت یک callback نیست:

۱. **فانکشن‌ها** › **فانکشن‌اپ تازه** بسازید (یا یک فانکشن به اپ فانکشن موجودتان اضافه کنید).
۲. فانکشنی با تریگر **HTTP** بسازید. کدش فعلاً کافی است فقط بدنهٔ دریافتی را لاگ کند — کاوه‌نگار
   دقیق‌ترین شکل بدنه را در همان تنظیمات Status Callback URL توضیح نمی‌دهد، پس ساده‌ترین راهِ درست،
   دیدن payload واقعی در همان اولین callback است، نه حدس زدنش از قبل.
۳. تیک **عمومی** را بزنید، **ذخیره** و **انتشار** کنید.
۴. آدرس فانکشن را در پنل کاوه‌نگار، تنظیمات خط، به‌عنوان Status Callback URL ثبت کنید.
۵. یک پیامک آزمایشی بفرستید و به **لاگ‌های اپ** فانکشن سر بزنید — payload واقعی همان‌جاست.

همان قاعدهٔ فصل قبل: پنل تماس‌های عمومی را نمی‌بیند، پس این تماس‌ها را در **لاگ‌های اپ** پیدا
می‌کنید، نه در تاریخچهٔ اجراهای فانکشن.

---

قدم بعدی: [آماده‌سازی اپ برای Deploy با CLI](12-cli-deploy.md)
