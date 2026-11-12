# ۱۲ — آماده‌سازی اپ برای Deploy با CLI

این فصل مکمل بخش «Harbora CLI» در [فصل ۳](03-applications.md) است: آنجا فقط گفته شد که یک اپ را
می‌شود از سیستم خودتان با CLI push کرد؛ اینجا می‌گوید **قبل از اولین deploy چه چیزهایی باید آماده
باشد** — به‌ازای هر نوع پروژه — تا کسی که پنجمین اپش را deploy می‌کند دیگر لازم نباشد از کسی
بپرسد. مرجع کامل CLI و API در [`docs/cli-deploy.md`](../cli-deploy.md) است؛ این فصل نسخهٔ
پیش‌بارگذاری‌شدهٔ همان سند است.

این فصل از یک اتفاق واقعی نوشته شده: یک اپ .NET با یک قدم build فرانت‌اند، دوبار deploy‌اش شکست
خورد. علتش باگی در Harbora بود — بستهٔ آپلود هر مسیری که نامش «build» بود را کنار می‌گذاشت، حتی اگر
آن پوشه یک پوشهٔ سورس معمولی بود، نه خروجی build. آن باگ رفع شده؛ اما درسِ آن باقی است: چیزی که قرار
است روی سرور ساخته شود، باید *دقیقاً* همان چیزی باشد که به سرور می‌رسد، و اگر بخشی از آن حذف شود،
هیچ‌جای گزارش deploy این را با یک جملهٔ واضح نمی‌گفت. حالا می‌گوید — هم در آپلود، هم در `harbora
doctor` که همین فصل معرفی می‌کند.

## قبل از هرچیز: `harbora doctor`

```bash
harbora doctor
```

این دستور تازه است و دقیقاً برای همین لحظه ساخته شده: قبل از اولین deploy یک اپ تازه، یا هر وقت یک
deploy با خطایی گیج‌کننده شکست خورد. چیزهایی که بررسی می‌کند و برای هرکدام می‌گوید چه دیده و چه
نتیجه گرفته:

* **`harbora.yml`** — پارس می‌شود؛ اپ و سرور مشخص است.
* **Build** — مسیر `context` وجود دارد؛ `Dockerfile` وجود دارد یا stack به‌طور خودکار تشخیص داده
  می‌شود (Node، .NET‏، Go، PHP، Python، استاتیک)؛ هر مسیر `COPY` داخل Dockerfile زیر همان context
  واقعاً وجود دارد؛ build به `$PORT` توجه می‌کند.
* **Upload** — دقیقاً همان چیزی که `--push` می‌فرستد را می‌سازد و می‌گوید چند فایل و کدام‌ها کنار
  گذاشته می‌شوند و چرا؛ سپس هر مسیر `COPY` و هر اسکریپت `package.json` را با همان فهرستِ حذف‌شده‌ها
  مقایسه می‌کند. **این همان چکی است که می‌توانست اتفاق بالا را قبل از آپلود بگیرد.**
* **فایل‌های env** — فقط در اجرای مستقیم `harbora doctor` — نام هر فایل env با مقدار واقعی که آپلود
  روی سیستم شما نگه می‌دارد، و هر فایلی که یک خط `!` به‌جایش می‌فرستد. اگر چیزی جز `.env` و `.env.local`
  بماند هشدار می‌دهد، چون ممکن است build آن را بخواند (بخش «فایل‌های env» پایین‌تر). Dockerfileی که یکی
  از این‌ها را `COPY` کند، در چک Upload شکست می‌خورد و می‌گوید به‌جایش چه کنید.
* **ورود (auth)** — فقط در اجرای مستقیم `harbora doctor`، نه در پیش‌بررسی خودکار داخل `deploy` —
  اینکه نشست فعلی روی همان سرور هنوز معتبر است.

`harbora deploy` همین چک‌های build/upload را خودش، خودکار، قبل از آپلود اجرا می‌کند و اگر چیزی
واقعاً deploy را می‌شکند، قبل از فرستادن حتی یک بایت متوقف می‌شود. اگر یک چک برای پروژهٔ خاص شما
اشتباه تشخیص داد، با `harbora deploy --skip-doctor` رد شوید.

## چک‌لیست `harbora.yml`

```yaml
app: my-api                          # اسلاگ اپ روی سرور
server: https://panel.example.com    # اختیاری؛ وگرنه سروری که آخرین بار وارد آن شده‌اید

build:
  dockerfile: Dockerfile             # مسیر داخل context
  context: .                         # ریشهٔ build

ignore:                              # علاوه بر .dockerignore / .gitignore
  - coverage
```

* **`app`** — بدون آن، deploy با «No app specified» شکست می‌خورد؛ یا این را بنویسید یا اسلاگ را
  روی خط فرمان بدهید: `harbora deploy <slug>`.
* **`server`** — اگر ننویسید، همان سروری استفاده می‌شود که آخرین بار با `harbora login` واردش
  شده‌اید.
* **`build.dockerfile` / `build.context`** — فقط وقتی لازم است که Dockerfile خودتان دارید یا در
  مسیر غیرِپیش‌فرض است. اگر بنویسید ولی فایل واقعاً آنجا نباشد، `harbora doctor` این را می‌گیرد.
* **`ignore`** — مسیرهای اضافه‌ای که باید از آپلود کنار بمانند، روی `.dockerignore`/`.gitignore`.

## کِی auto-detect کافی است، کِی Dockerfile خودتان لازم است

Harbora بدون Dockerfile هم stack را حدس می‌زند: Node (`package.json`)، .NET (`*.csproj`)، Go
(`go.mod`)، PHP (`composer.json`/`index.php`)، Python (`requirements.txt`/`pyproject.toml`/
`Pipfile`)، استاتیک (`index.html`). در این حالت `ENV PORT` هم خودکار تنظیم می‌شود.

یک `package.json` که نه اسکریپت `start` دارد و نه کنارش `server.js` هست، چیزی را اجرا نمی‌کند؛ پس اگر
.NET، Go، PHP یا Python هم در پروژه باشد کنار می‌رود و همان stack انتخاب می‌شود، چون این فایل مال
فرانت‌اند آن اپ است.

اگر هیچ‌کدام از این‌ها نباشد و `package.json` اسکریپت `build` داشته باشد، پروژه یک فرانت‌اند است (مثلاً
Vite): build اجرا می‌شود و خروجی‌اش را nginx روی پورت اپ سرو می‌کند. اگر build شکست بخورد یا
`index.html` تولید نکند، deploy هم شکست می‌خورد. اپی از نوع Worker یا با start command خودش همان Node
می‌ماند. متغیرهای `VITE_*`، `REACT_APP_*`، `NEXT_PUBLIC_*`، `PUBLIC_*`، `VUE_APP_*`، `GATSBY_*` و
`NUXT_PUBLIC_*` هنگام build خوانده می‌شوند، روی Node هم؛ مقدارشان در فایل‌هایی می‌رود که هر بازدیدکننده‌ای
می‌بیند، و برای عوض کردنشان deploy دوباره لازم است. اسکریپت `start` که دقیقاً dev server یک قالب باشد
(مثل `react-scripts start` یا `vite`) وقتی `build` هم هست start حساب نمی‌شود، چون داخل container کار نمی‌کند.

اما auto-detect فقط یک دستور می‌زند — برای .NET یعنی `dotnet publish`، برای Node یعنی `npm ci` و
اجرای اسکریپت start. اگر پروژه‌تان **بیش از یک قدم** لازم دارد (مثلاً build فرانت‌اند جدا از publish
بک‌اند)، auto-detect چیزی نیم‌ساخته تحویل می‌دهد — نه خطا، فقط یک اپ بدون CSS. اگر `package.json`
اسکریپت `build` داشته باشد، گزارش deploy یک هشدار دربارهٔ همین نشان می‌دهد. اینجا دقیقاً وقتی است که
خودتان یک Dockerfile چندمرحله‌ای بنویسید.

## `$PORT`

Harbora برای container شما متغیر محیطی `PORT` را تنظیم می‌کند و انتظار دارد اپ روی همان پورت گوش
بدهد. build‌های auto-detect این را خودشان رعایت می‌کنند. **در Dockerfile دلخواه، خودتان مسئولش
هستید** — گوش‌ندادن به `$PORT` یعنی deploy با موفقیت تمام می‌شود ولی اپ ۵۰۲ می‌دهد، چون container
دارد روی پورتی گوش می‌دهد که Traefik به آن مسیر نمی‌دهد.

```dockerfile
# استفاده از exec هم لازم است: بدونش dotnet/node به‌جای PID 1 زیرِ /bin/sh اجرا می‌شود، و سیگنال
# توقف موقع هر deploy بعدی به آن نمی‌رسد — یعنی هر بار به‌جای خاموشی تمیز، منتظر kill timeout می‌مانید.
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet App.dll --urls http://0.0.0.0:${PORT:-8080}"]
```

## چه چیزی از آپلود کنار گذاشته می‌شود

دو دسته قانونِ داخلی، بدون نیاز به هیچ فایل ignore:

* **در هر عمق** — چون هیچ‌کس سورس واقعی داخل پوشه‌ای با این اسم‌ها نمی‌گذارد: `node_modules`،
  `.git`، `bin`، `obj`، `.venv`، `.next`، `.vs`، `.env` و چند مورد مشابه.
* **فقط در ریشهٔ پروژه** — `build`، `dist`، `target`، `vendor`، `.output`. این‌ها دقیقاً نام‌های
  پیش‌فرض خروجی build هستند (`./build` یا `./dist` در npm، `./target` در Cargo، `./vendor` در
  PHP/Go)، ولی هرکدام می‌توانند در عمقِ دیگری از درخت، یک پوشهٔ سورس کاملاً معمولی هم باشند.

**اتفاقی که این فصل را نوشت** دقیقاً همین بود: یک فایل کمکیِ سورس به اسم `Scripts/build/
copy-fonts.mjs` — دو سطح پایین‌تر از ریشه، داخل پوشه‌ای به اسم `build` — با نسخهٔ قدیمی‌ترِ این
قانون (که «build» را در هر عمق می‌گرفت) بی‌صدا حذف می‌شد. حالا این قانون فقط در ریشه اثر دارد، پس
یک پوشهٔ تودرتو با همین اسم دیگر قربانی نمی‌شود.

ولی این تمام ماجرا نیست: **`ignore:` در harbora.yml و هر خط داخل `.gitignore`/`.dockerignore` خودِ
شما، همچنان در هر عمق اثر می‌کنند** — همین رفتار است که باعث می‌شود `ignore: [coverage]` هرجا
`coverage/` باشد را حذف کند، و این دقیقاً همان قابلیتی است که این ابزار برایش ساخته شده. اگر
`.gitignore` پروژهٔ شما یک خط ساده مثل `build` یا `dist` داشته باشد — که در پروژه‌های جاوااسکریپت
خیلی رایج است — یک پوشهٔ تودرتو با همین اسم، دوباره، از راهی دیگر، حذف می‌شود.

دو راه برای جلوگیری:

1. قبل از اولین deploy یک اپ تازه، `harbora doctor` را اجرا کنید (یا فقط `harbora deploy`، که خودش
   همین را اجرا می‌کند) — `package.json` و مسیرهای `COPY` در Dockerfile را می‌خواند و اگر به مسیری
   اشاره کنند که آپلود حذفش می‌کند، قبل از آپلود متوقف می‌شود.
2. اگر واقعاً یک پوشهٔ تودرتو به همین اسم‌ها دارید، ورودی ignore خودتان را به ریشه محدود کنید:
   `/build` به‌جای `build`.

### فایل‌های env

هر فایل env که مقدار واقعی دارد روی سیستم خودتان می‌ماند: `.env`، `.env.local`، `.env.production`،
`.env.staging` و هر `.env.<نام>` دیگری، در هر عمقی. فایل‌های نمونه آپلود می‌شوند، چون Launch نام
متغیرها را از آن‌ها می‌خواند: `.env.example`، `.env.sample`، `.env.template`، `.env.dist`،
`env.example`، `example.env`، `sample.env` و هر `.env.<نام>` که به `.example`، `.sample`، `.template`
یا `.dist` ختم شود، مثل `.env.local.example`. `harbora deploy` و `harbora launch` نام هر فایلی را که
نگه داشتند می‌گویند، و `harbora doctor` همین را پیش از هر دو. zipی که از مرورگر فرستاده شود هم همین
قانون را دارد، و لاگ deploy نام چیزی را که کنار گذاشته شد می‌برد.

مقدارهای production جایشان در متغیرهای اپ است، و build هم از همان‌جا می‌خواند: در build خودکار، نامی
عمومی مثل `VITE_` یا `NEXT_PUBLIC_` خودش به build می‌رسد. این مهم است، چون Vite، Next.js و Create
React App فایل `.env.production` را هنگام build می‌خوانند؛ پروژه‌ای که به آن فایل تکیه داشت، تا وقتی
مقدارها روی اپ تنظیم نشده‌اند، بدون آن‌ها ساخته می‌شود.

اگر فایلی واقعاً باید برود، یک خط `!` که نامش را ببرد آن را رد می‌کند: `!.env.production`، یا با
مسیر: `!apps/web/.env.production`. اگر پروژه `.dockerignore` دارد خط را همان‌جا بنویسید، چون Docker هم
همان فایل را می‌خواند و خطی مثل `.env*` در آن، فایلی را که CLI فرستاده باز هم از build بیرون نگه
می‌دارد. وگرنه زیر `ignore:` در `harbora.yml` بنویسید، داخل گیومه: `- "!.env.production"`؛ ساختن یک
`.dockerignore` فقط برای این کار جای `.gitignore`ی را می‌گیرد که آپلود می‌خواند. در zip، `.dockerignore`
خود پروژه (یا اگر نباشد `.gitignore`) خوانده می‌شود، نه `harbora.yml`. خط `!` فقط فایل env را
برمی‌گرداند، و هیچ‌وقت `.env` یا `.env.local` را؛ و هرچه بفرستد، هر کسی که build را ببیند می‌تواند
بخواند.

deploy از Git مخزن را clone می‌کند، پس `.env.production`ی که commit شده در آن build هست، هرچه این
قانون‌ها بگویند؛ `--branch` هم همان محتوای commitشده را می‌فرستد.

## راهنمای هر نوع پروژه

### ۱. اپ .NET با یک قدم build فرانت‌اند

نمونهٔ دقیقاً همین حالت. auto-detect برای .NET فقط `dotnet publish` می‌زند — باندل Vite یا فونت
پینشده هیچ‌وقت ساخته نمی‌شود و اپ بدون CSS بالا می‌آید، بی‌آنکه چیزی در گزارش deploy این را بگوید.
راه‌حل، یک Dockerfile چندمرحله‌ای خودتان است:

```dockerfile
# ۱. فرانت‌اند
FROM node:22-alpine AS assets
WORKDIR /web
COPY src/Web/package.json src/Web/package-lock.json ./
RUN npm ci
COPY src/Web/ ./
RUN npm run build

# ۲. Publish
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Web/Web.csproj src/Web/
RUN dotnet restore src/Web/Web.csproj
COPY src/ src/
COPY --from=assets /web/wwwroot/build src/Web/wwwroot/build
RUN dotnet publish src/Web/Web.csproj -c Release --no-restore -o /app

# ۳. Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet Web.dll --urls http://0.0.0.0:${PORT:-8080}"]
```

```yaml
build:
  dockerfile: Dockerfile
  context: .
```

نکته‌ها: خروجی Vite را در image بسازید، نه از قبل روی سیستم خودتان — چون همان خروجی معمولاً داخل
`wwwroot/build` یا `wwwroot/dist` می‌نشیند، و همان‌ها دقیقاً نام‌هایی هستند که در ریشهٔ پروژه از
آپلود کنار گذاشته می‌شوند؛ ساختنش داخل مرحلهٔ `assets` این مشکل را کلاً کنار می‌زند. اگر
`package.json` اسکریپت `prebuild`/`fonts`/مشابه دارد که فایلی از داخل خودِ سورس (نه `node_modules`)
می‌خواند، `harbora doctor` آن را چک می‌کند.

### ۲. اپ Node ساده

معمولاً auto-detect کافی است: `package.json` پیدا می‌شود، `npm ci` و اسکریپت `start` اجرا می‌شوند،
و `PORT` خودکار تنظیم می‌شود (`process.env.PORT` را بخوانید).

Dockerfile خودتان وقتی لازم می‌شود که: پکیج‌های native دارید که نیاز به toolchain ساخت دارند، یک
monorepo با چند package.json هستید، یا دستور start شما چیزی پیچیده‌تر از `npm start` است (مثلاً یک
مرحلهٔ build جدا مثل TypeScript که باید قبل از اجرا کامپایل شود).

```yaml
app: my-node-app
build:
  dockerfile: Dockerfile
  context: .
```

### ۳. سایت استاتیک

اگر پوشه فقط `index.html` و فایل‌های کنارش را دارد، auto-detect به‌عنوان استاتیک تشخیصش می‌دهد و با
Nginx سرو می‌شود — هیچ Dockerfile لازم نیست.

اگر یک ابزار build (Vite، webpack، Hugo، …) دارید که HTML نهایی را می‌سازد، دو راه هست:

* یک `dockerfileLines:` کوتاه در harbora.yml که build را داخل image اجرا می‌کند و خروجی را با Nginx
  سرو می‌کند — همان چیزی که یک Dockerfile چندمرحله‌ای انجام می‌دهد، فقط بدون فایل جدا.
* یک Dockerfile واقعی، وقتی مراحل build پیچیده‌تر می‌شوند.

در هر دو حالت: خروجی build را از قبل روی سیستم خودتان نسازید و آپلود نکنید — همان تلهٔ پوشهٔ
`build`/`dist` در ریشه، اینجا هم صادق است. بسازیدش داخل image.

### ۴. اپ با Dockerfile دلخواه (هر stack)

چک‌لیست کوتاه، مستقل از زبان:

1. مسیرهای `COPY` نسبت به `context` واقعاً وجود دارند.
2. `$PORT` خوانده می‌شود، نه یک پورت ثابت.
3. `ENTRYPOINT`/`CMD` با فرم exec نوشته شده (`["cmd", "arg"]` یا `sh -c "exec …"`)، نه فرم shell
   ساده — وگرنه سیگنال توقفِ هر deploy بعدی به فرآیند اصلی نمی‌رسد.
4. هرچه در `package.json`/اسکریپت‌های build از یک مسیر سورس نیاز دارید، همان مسیر باید در آپلود
   باشد — `harbora doctor` را قبل از اولین deploy اجرا کنید تا مطمئن شوید.

## deploy با هر push، از CI

دو فایل آماده در [`examples/ci/`](../../examples/ci/) هست، یکی برای GitHub Actions و یکی برای GitLab
CI. هرکدام دو secret لازم دارد و دیگر هیچ:

1. **یک توکن.** در پنل از منوی حساب کاربری به **«تنظیمات»** بروید، در بخش **«توکن‌های API»** یک نام
   بدهید (مثلاً `ci`) و **«ساخت»** را بزنید. توکن فقط همان یک بار نشان داده می‌شود، پس همان موقع
   کپی‌اش کنید. این توکن به‌جای حساب خودتان، در فضای کاری شخصی‌تان، کار می‌کند.
2. **دو secret در مخزن:** `HARBORA_SERVER` با آدرس پنل (مثل `https://panel.example.com`) و
   `HARBORA_TOKEN` با همان توکن. در GitHub زیر *Settings → Secrets and variables → Actions*؛ در GitLab
   زیر *Settings → CI/CD → Variables*، با توکنِ masked.
3. **کدام اپ.** `harbora deploy` اپ را از `app:` در `harbora.yml` می‌خواند، پس این فایل را commit کنید
   (`harbora init` آن را می‌سازد)، یا اسلاگ را در خود دستور بنویسید: `harbora deploy my-app --push …`.
4. **خود فایل.** `examples/ci/github-actions.yml` می‌شود `.github/workflows/harbora.yml`، و
   `examples/ci/gitlab-ci.yml` می‌شود `.gitlab-ci.yml`.

GitHub Actions، بدون توضیح‌هایش:

```yaml
name: Deploy to Harbora

on:
  push:
    branches: [main]

concurrency:
  group: harbora-deploy
  cancel-in-progress: false

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Install the Harbora CLI
        run: curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install-cli.sh | bash

      - name: Deploy
        env:
          HARBORA_SERVER: ${{ secrets.HARBORA_SERVER }}
          HARBORA_TOKEN: ${{ secrets.HARBORA_TOKEN }}
        run: harbora deploy --push --yes --server "$HARBORA_SERVER" --token "$HARBORA_TOKEN"
```

GitLab CI، بدون توضیح‌هایش:

```yaml
deploy:
  stage: deploy
  image: debian:bookworm-slim
  resource_group: harbora
  rules:
    - if: $CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH
  before_script:
    - apt-get update -qq && apt-get install -y -qq --no-install-recommends curl ca-certificates > /dev/null
    - curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install-cli.sh | bash
  script:
    - harbora deploy --push --yes --server "$HARBORA_SERVER" --token "$HARBORA_TOKEN"
```

همه‌ی کار را همان یک دستور می‌کند:

* `--push` همان commitی را می‌فرستد که job گرفته، چه اپ مخزن Git داشته باشد چه نه؛ پس چیزی که deploy
  می‌شود دقیقاً همان است که push شد. بدون آن، اپی که مخزن Git دارد را سرور از شاخه‌ی پیش‌فرضش می‌کشد.
* `--server` و `--token` جای `harbora login` را می‌گیرند، پس اطلاعات ورود روی runner ذخیره نمی‌شود. وقتی
  ترمینالی نیست `--yes` خودبه‌خود فرض می‌شود؛ اینجا نوشته شده تا دستور هیچ‌وقت منتظر جواب نماند.
* دستور build را تا آخر دنبال می‌کند: اگر deploy موفق شود با `0` تمام می‌شود و اگر شکست بخورد، لغو
  شود یا برگردانده شود با `1`؛ پس deploy شکست‌خورده job را هم شکست می‌دهد. `--no-follow` همین که deploy
  در صف قرار گرفت با `0` برمی‌گردد و چیزی درباره‌ی موفق‌بودنش نمی‌گوید.
* `concurrency` در GitHub و `resource_group` در GitLab هر بار فقط یک deploy را اجرا می‌کنند. بدون آن‌ها،
  آپلودی که وقتی build همین اپ هنوز در جریان است برسد، رد می‌شود.
* `.env.production`ی که commit شده در checkout هست ولی آپلود نمی‌شود (بخش «فایل‌های env») و لاگ job
  نامش را می‌برد. مقدارهایش را در متغیرهای اپ بگذارید.

اپی که به مخزن Git وصل است، بدون هیچ CIی هم می‌تواند با هر push از بخش «استقرار خودکار (Git)» در
صفحه‌ی خودش deploy شود.

## جمع‌بندی: قبل از اولین deploy یک اپ تازه

هنوز اپی در پنل ندارید؟ `harbora launch` اپ و دیتابیسش را هم می‌سازد: [فصل ۱۵](15-launch.md). برای اپی که هست:

```bash
harbora init      # اگر harbora.yml هنوز نیست
harbora doctor    # قبل از فرستادن چیزی، ببینید چه چیزی فرستاده می‌شود و چه چیزی نه
harbora deploy    # همین چک‌ها را خودش دوباره، خودکار، قبل از آپلود اجرا می‌کند
```

---

برگشت به [فهرست](README.md)
