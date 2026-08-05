"""
Fetches the ready-app logos.

There was a script here that *drew* them: a simplified rendering of each project's mark, in the
project's colour, on the reasoning that a drawing is not a third-party binary and cannot change
under us. That reasoning was wrong in the way that mattered — the marks were not the projects'
marks. A logo that is nearly right is not a logo, it is a guess with a brand colour on it.

These come from the Simple Icons set, which publishes each project's own path data under CC0. The
marks themselves stay the projects' trademarks; using them to identify the application they launch
is what the licence note on each asset records.

Run it from a machine with network access:  python3 scripts/fetch-app-logos.py
"""
import json, re, pathlib, urllib.request

VERSION = 13
CDN = f"https://cdn.jsdelivr.net/npm/simple-icons@{VERSION}"
DEST = pathlib.Path(__file__).resolve().parents[1] / "src/Harbora.Web/wwwroot/img/apps"

# Template key -> Simple Icons slug. Two keys share a mark on purpose: redis-commander is a UI for
# Redis, and docker-workspace is a Docker workspace.
ICONS = {
    "n8n": "n8n", "docker-workspace": "docker", "sentry": "sentry", "gitea": "gitea",
    "rocketchat": "rocketdotchat", "minio": "minio", "grafana": "grafana", "metabase": "metabase",
    "postgres": "postgresql", "mysql": "mysql", "mariadb": "mariadb", "redis": "redis",
    "mongodb": "mongodb", "wordpress": "wordpress", "ghost": "ghost", "laravel": "laravel",
    "node": "nodedotjs", "aspnet": "dotnet", "meilisearch": "meilisearch",
    "nginx-static": "nginx", "redis-commander": "redis", "uptime-kuma": "uptimekuma",
    "rabbitmq": "rabbitmq", "nats": "natsdotio",
}


def slug_of(icon):
    if "slug" in icon:
        return icon["slug"]
    title = icon["title"].lower().replace("+", "plus").replace(".", "dot").replace("&", "and")
    return re.sub(r"[^a-z0-9]", "", title)


def main():
    with urllib.request.urlopen(f"{CDN}/_data/simple-icons.json") as response:
        data = json.load(response)

    icons = data["icons"] if isinstance(data, dict) else data
    colours = {slug_of(i): i["hex"] for i in icons}

    for key, slug in sorted(ICONS.items()):
        with urllib.request.urlopen(f"{CDN}/icons/{slug}.svg") as response:
            svg = response.read().decode("utf-8")

        # The project's own colour on the root, so every path inherits it and the mark reads the way
        # the project publishes it rather than as a silhouette.
        svg = svg.replace("<svg ", f'<svg fill="#{colours[slug]}" ', 1)
        svg = svg.replace('role="img" ', "", 1)

        (DEST / f"{key}.svg").write_text(svg, encoding="utf-8")
        print(f"{key:18} {slug:16} #{colours[slug]}")


if __name__ == "__main__":
    main()
