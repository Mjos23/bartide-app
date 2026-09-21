"""Bounded source checks and real SQLite backup checks; not a Docker runtime test."""
import importlib.util
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / "deploy/digitalocean"
checks = []


def check(condition, label):
    assert condition, label
    checks.append(label)


compose = (HOST / "compose.yaml").read_text()
for key, value in {
    "Auth__AllowLocalTestProvider": '"false"', "Media__AllowLocalStore": '"false"',
    "Notifications__Mode": "disabled", "Notifications__AllowLocalProvider": '"false"',
    "Notifications__LocalEndpoint": '""', "ServiceBilling__Environment": "sandbox",
    "ServiceBilling__LiveEnabled": '"false"', "ServiceBilling__CheckoutEnabled": '"false"',
    "ServiceBilling__CardsOnlyVerified": '"false"', "ServiceBilling__AllowLocalTestProvider": '"false"',
    "ServiceBilling__DevelopmentApiBase": '""', "MerchantPayments__OnboardingEnabled": '"false"',
    "MerchantPayments__CheckoutEnabled": '"false"', "MerchantPayments__CardsOnlyVerified": '"false"',
    "MerchantPayments__AllowLocalTestProvider": '"false"', "MerchantPayments__ApiBaseUrl": '""',
    "WebPush__Enabled": '"false"', "WebPush__AllowDevelopmentLoopback": '"false"',
    "WebPush__DevelopmentPushOrigin": '""',
}.items():
    check(f"      {key}: {value}\n" in compose, f"Compose explicitly fixes {key}")
check(compose.count("DOTNET_ENVIRONMENT: Production") == 2 and compose.count("ASPNETCORE_ENVIRONMENT: Production") == 2, "Both environment selectors fixed on API and Web")
check("RestrictedKey:" not in compose and "WebhookSecret:" not in compose, "Compose preserves external reconciliation credentials")
check(compose.count("    ports:") == 1 and '"80:8080"' in compose and '"443:8443"' in compose, "Only proxy publishes host ports")
check("sqlite_data:/data" in compose and "blazor_keys:/keys" in compose, "Database and protection keys persist")
check("max-size: \"10m\"" in compose and "max-file: \"3\"" in compose, "Container log files bounded")
sdk = json.loads((ROOT / "global.json").read_text())["sdk"]["version"]
for name in ["api", "web"]:
    image = (HOST / f"Dockerfile.{name}").read_text()
    check(f"ARG DOTNET_SDK_VERSION={sdk}" in image, f"{name} SDK matches global.json")
    check("USER $APP_UID" in image and "chmod 0700" in image, f"{name} non-root and private volume directory")
    check("--no-restore" in image and "-c Release" in image, f"{name} declares Release publish")
example = (HOST / "api.runtime.env.example").read_text(encoding="utf-8-sig")
for line in example.splitlines():
    if line and not line.startswith("#") and any(s in line.split("=", 1)[0] for s in ["Key", "Secret", "PlatformOwnerUserId"]):
        check(line.endswith("="), f"Blank example credential: {line.split('=')[0]}")
spec = importlib.util.spec_from_file_location("backup", HOST / "backup-sqlite.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
with tempfile.TemporaryDirectory(prefix="tide-hosting-synthetic-") as directory:
    folder = Path(directory)
    source = folder / "live.db"
    with closing(sqlite3.connect(source)) as live:
        live.execute("PRAGMA journal_mode=WAL")
        live.executescript("CREATE TABLE parent(id INTEGER PRIMARY KEY); CREATE TABLE child(id INTEGER PRIMARY KEY,parent_id INTEGER REFERENCES parent(id)); INSERT INTO parent VALUES(1); INSERT INTO child VALUES(2,1);")
        live.commit()
        check(Path(str(source) + "-wal").stat().st_size > 0, "Synthetic database has committed WAL content")
        target = folder / "backup.db"
        module.backup(source, target)
        with closing(sqlite3.connect(target)) as db:
            check(db.execute("SELECT * FROM child").fetchall() == [(2, 1)], "Online backup includes committed WAL rows")
        restored = folder / "restored.db"
        module.backup(target, restored)
        with closing(sqlite3.connect(restored)) as db:
            check(db.execute("PRAGMA integrity_check").fetchone() == ("ok",), "Restore to new file passes integrity")
            check(db.execute("PRAGMA foreign_key_check").fetchall() == [], "Restore preserves foreign keys")
        for original, output, label in [(source, target, "existing destination"), (source, source, "same source/destination"), (folder / "absent.db", folder / "new.db", "missing source")]:
            try:
                module.backup(original, output)
            except (FileExistsError, FileNotFoundError, ValueError):
                check(True, f"Reject {label}")
            else:
                raise AssertionError(f"Did not reject {label}")
        check(not (folder / "absent.db").exists(), "Missing source is never silently created")
print(json.dumps({"passed": len(checks), "checks": checks, "limits": "Source assertions, not YAML/Compose parsing or Docker/Caddy/Linux runtime verification; synthetic backup only."}, indent=2))
