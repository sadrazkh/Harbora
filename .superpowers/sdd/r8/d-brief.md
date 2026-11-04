# D — The inspector: read a source tree and say what it needs

Read `.superpowers/sdd/r8/CONSTRAINTS.md` and `docs/superpowers/specs/2026-11-04-launch-design.md` first.

You own `src/Harbora.Shared/Inspection/` and `tests/Harbora.Tests/SourceInspectorTests.cs` (split into more test
files if it helps). The record shapes in `SourceInspection.cs` are the contract; `SourceInspector.Inspect` is a
stub you replace. Split the implementation into small files under that folder. `Harbora.Shared` references
nothing and must stay that way (the CLI uses it): BCL only, `System.Text.Json` is fine, no YAML or TOML package —
read those formats with careful line scanning. No I/O beyond reading the given tree. No network, no process.

## Rules of the road

- **Never throws on a bad tree.** Malformed `package.json`, binary junk, unreadable file, missing directory: a note
  or nothing, never an exception. Files over 1 MiB are not read. Skip `.git`, `node_modules`, `vendor`, `bin`, `obj`,
  `dist`, `build`, `.next`, `.venv`, `venv`, `__pycache__`, `target`. Bound depth and entry count: a huge tree must not
  take long or hold much memory.
- **Never reads a real `.env`.** Only example files. If `.env` is present, add note `env-file-ignored`.
- **Proposes, does not decide.** Weak evidence gives `Certain = false`.

## Root directory

Descend through single-child folders (a GitHub zip's `repo-main/`). If the root then holds nothing recognisable but
exactly one immediate child does, that child is `RootDirectory`. If several do, list them in `RootCandidates`, add note
`several-apps-found`, and leave `RootDirectory` null. Paths use `/`. Everything else is judged from the chosen root.

## Stack

A `Dockerfile` wins. Then **exactly the order `Buildpacks.Detect` uses** (read
`src/Harbora.Infrastructure/Deployments/Buildpacks.cs`, including `FindCsproj`) — add a parity test in
`Harbora.Tests` over fixture trees so the two cannot disagree. Compose only when there is neither a Dockerfile nor a
buildpack match: then `Stack = Compose`, no `Services` (the stack runs its own), note `compose-runs-own-services`.
Nothing recognised: `Unknown` and note `no-stack-recognised`, reusing the wording of `Buildpacks.DetectionHint` where it
has something more specific to say (Django is deliberately unsupported there; say so, note `django-unsupported`).

## Kind, start command

- `Procfile`: a `web:` line means Web; only `worker:` means Worker, with that line as `StartCommand`.
- .NET: `Microsoft.NET.Sdk.Worker` is Worker; `Microsoft.NET.Sdk.Web` is Web.
- Bot and consumer libraries with **no** web framework beside them make it a Worker: Python (`python-telegram-bot`,
  `aiogram`, `pytelegrambotapi`, `telethon`, `discord.py`, `celery`) against (`flask`, `django`, `fastapi`, `uvicorn`,
  `gunicorn`, `aiohttp`, `starlette`); Node (`telegraf`, `grammy`, `node-telegram-bot-api`, `discord.js`, `bullmq` worker
  scripts) against (`express`, `fastify`, `koa`, `next`, `nuxt`, `@nestjs/core`, `hono`). Otherwise Web.

## Port

Null for a Worker. `EXPOSE` in the Dockerfile, else `PORT=` in the example env file, else the stack's default — take
these from what `Buildpacks` generates and how its images listen; do not invent a number that disagrees with it. A
framework's well-known default (Next and Express 3000, uvicorn 8000, Flask 5000) only where the buildpack does not set
`PORT` for it. Say which in a code comment.

## Services

Each with `ServiceEvidence(file, reason)`, e.g. `("package.json", "\"pg\" in dependencies")`.

| Kind | Node | Python | .NET PackageReference | Go (go.mod) | PHP (composer) |
|---|---|---|---|---|---|
| Postgres | `pg`, `postgres`, `pg-promise` | `psycopg2`, `psycopg2-binary`, `psycopg`, `asyncpg` | `Npgsql*` | `lib/pq`, `jackc/pgx` | `ext-pgsql` |
| MySql | `mysql`, `mysql2` | `mysqlclient`, `pymysql`, `aiomysql`, `mysql-connector-python` | `MySqlConnector`, `MySql.Data`, `Pomelo.EntityFrameworkCore.MySql` | `go-sql-driver/mysql` | `ext-mysqli`, `ext-pdo_mysql` |
| MariaDb | `mariadb` | `mariadb` | | | |
| Redis | `redis`, `ioredis`, `bull`, `bullmq` | `redis`, `aioredis`, `rq` | `StackExchange.Redis` | `redis/go-redis`, `gomodule/redigo` | `predis/predis` |
| MongoDb | `mongoose`, `mongodb` | `pymongo`, `motor`, `mongoengine`, `beanie` | `MongoDB.Driver` | `go.mongodb.org/mongo-driver` | `mongodb/mongodb` |
| RabbitMq | `amqplib`, `amqp-connection-manager` | `pika`, `aio-pika` | `RabbitMQ.Client`, `MassTransit.RabbitMQ` | `rabbitmq/amqp091-go`, `streadway/amqp` | `php-amqplib/php-amqplib` |
| Nats | `nats` | `nats-py` | `NATS.Client`, `NATS.Net` | `nats-io/nats.go` | |
| Meilisearch | `meilisearch` | `meilisearch` | `Meilisearch` | `meilisearch/meilisearch-go` | `meilisearch/meilisearch-php` |

All of those are `Certain = true`. Also:

- Prisma: `datasource … provider = "postgresql" | "mysql" | "mongodb"` in `prisma/schema.prisma` is certain.
- Laravel: `DB_CONNECTION=pgsql|mysql|mariadb` in the example file is certain.
- `DATABASE_URL=` with a `postgres://`, `mysql://` or `mongodb://` value; `REDIS_URL`, `AMQP_URL`, `NATS_URL`, and any
  `docker-compose.yml` beside a Dockerfile or buildpack match whose `image:` is `postgres`, `mysql`, `mariadb`, `mongo`,
  `redis`, `rabbitmq`, `nats`, `getmeili/meilisearch`: certain.
- A bare example-file name with no value that implies a kind (`REDIS_HOST=`) is `Certain = false`.

Notes: `sqlite-needs-volume` when the project uses SQLite (`sqlite3`, `better-sqlite3`, `Microsoft.EntityFrameworkCore.Sqlite`,
Prisma `provider = "sqlite"`, Laravel `DB_CONNECTION=sqlite`): the file is lost on the next deploy unless a volume holds it.
`sql-server-unsupported` for `Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.Data.SqlClient`, `mssql`: Harbora offers no
SQL Server. `database-unclear` when generic `DB_*` names appear and nothing says which engine.

## Example env files

First found wins per name, in this order: `.env.example`, `.env.sample`, `.env.template`, `.env.dist`, `env.example`,
`example.env`, `sample.env`. Handle comments, blank lines, `export KEY=`, single and double quotes. Classify in this order:

1. **Dropped entirely** (not in `EnvVars`), because the platform owns them: `PORT` (still read for the port), `NODE_ENV`,
   `ASPNETCORE_ENVIRONMENT`, `ASPNETCORE_URLS`, `PYTHONUNBUFFERED`.
2. **ProvidedByAttach**: the exact names `ServiceCatalog.All[type].AttachEnv` sets for a detected service. Mirror that table
   in `Harbora.Shared` and add a test in `Harbora.Tests` that compares your table with the real `AttachEnv` output for every
   `ManagedServiceType`, so a new attach name cannot drift past it. Read `src/Harbora.Infrastructure/Services/ServiceCatalog.cs`.
3. **AliasOfAttach** with `Service` and `Role`: other spellings, e.g. Mongo `MONGO_URI`, `MONGO_URL`, `MONGODB_URL`; Postgres
   `POSTGRES_URL`, `POSTGRESQL_URL`, `PG_URL`, `POSTGRES_HOST`, `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`; Redis
   `REDIS_URI`, `REDIS_CONNECTION`, `CACHE_URL`; RabbitMq `RABBITMQ_URI`, `CLOUDAMQP_URL`; Nats `NATS_SERVER`, `NATS_SERVERS`;
   Meilisearch `MEILISEARCH_URL`, `MEILISEARCH_HOST`, `MEILI_HTTP_ADDR`; and the generic `DB_URL`, `DB_URI`, `DB_HOST`,
   `DATABASE_HOST`, `DB_PORT`, `DATABASE_PORT`, `DB_USER`, `DB_USERNAME`, `DATABASE_USER`, `DB_PASSWORD`, `DB_PASS`,
   `DATABASE_PASSWORD`, `DB_NAME`, `DB_DATABASE`, `DATABASE_NAME` against the **one** relational or Mongo service detected.
   Generic names with none, or more than one, detected: `NeedsAnswer`, plus note `database-unclear`.
   A generic name that is already an attach name for the detected engine (MariaDB sets `DB_HOST` itself) stays rule 2.
4. **GeneratedSecret**, exact names only: `SECRET`, `SECRET_KEY`, `APP_SECRET`, `APP_KEY`, `JWT_SECRET`, `JWT_KEY`,
   `JWT_SECRET_KEY`, `SESSION_SECRET`, `SESSION_KEY`, `COOKIE_SECRET`, `ENCRYPTION_KEY`, `AUTH_SECRET`, `NEXTAUTH_SECRET`,
   `DJANGO_SECRET_KEY`, `FLASK_SECRET_KEY`, `TOKEN_SECRET`, `ACCESS_TOKEN_SECRET`, `REFRESH_TOKEN_SECRET`, `SALT`, `HASH_SALT`.
   `ExampleValue` is null here: an example file's value is never a secret worth keeping.
5. **NeedsAnswer**: any name containing `KEY`, `TOKEN`, `SECRET`, `PASSWORD`, `PASS`, `API`, `CREDENTIAL` (a third party's
   credential, which only a person has); any empty or placeholder value (`changeme`, `your_…`, `xxx`, `<…>`, `...`, `TODO`,
   `example`); any value naming `localhost` or `127.0.0.1` (a development address that will not work here), keeping it in
   `ExampleValue` so the screen can show it.
6. **KeepExample**: everything else.

## Release command

Only where the tooling is unmistakable, otherwise null: Prisma with `prisma/migrations/` present → `npx prisma migrate deploy`;
an `artisan` file → `php artisan migrate --force`; `alembic.ini` → `alembic upgrade head`.

## Tests

Fixture trees in a temp folder, one per rule above, including: each stack; each row of the table (a theory); a GitHub-style
single top-level folder; a monorepo with one and with several apps; a Dockerfile plus a dev compose file with `postgres`; a
malformed `package.json`; a 2 MiB file ignored; `node_modules` holding a decoy `pg`; a real `.env` never read (put a value in
it and assert it appears nowhere in the result); every disposition; the two parity tests against `Buildpacks.Detect` and
`ServiceCatalog`. Harbora.Tests already references everything you need.

## Worktree

`E:/Cash.Net/source/repos/sadrazkh/Harbora-r8-d`, branch `r8/d`. Report: `.superpowers/sdd/r8/d-report.md`.
