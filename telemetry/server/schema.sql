-- Telemetria do GM IDE Android (Neon, projeto gmide-telemetry). Ver docs/telemetria.md.
-- Idempotente: pode rodar de novo depois de mudancas aditivas.
create schema if not exists gmide;

-- Lote recebido. O app reenvia o mesmo id quando a resposta se perde; o segundo envio e ignorado.
create table if not exists gmide.batches (
  id uuid primary key,
  install_id uuid not null,
  received_at timestamptz not null default now(),
  items int not null
);

create table if not exists gmide.installs (
  id uuid primary key,
  first_seen timestamptz not null default now(),
  last_seen timestamptz not null default now(),
  app_version text,
  device jsonb not null default '{}'
);

create table if not exists gmide.sessions (
  id uuid primary key,
  install_id uuid not null references gmide.installs(id) on delete cascade,
  started_at timestamptz not null,
  last_seen timestamptz not null,
  ended_at timestamptz,
  end_reason text,                       -- normal | crash | killed | restart | unknown
  app_version text,
  app_code int,
  runtime_version text,
  device_model text,
  android_api int,
  boot_ms int                            -- abertura ate o loop principal da IDE
);
create index if not exists sessions_started on gmide.sessions (started_at);

create table if not exists gmide.events (
  id bigint generated always as identity primary key,
  install_id uuid not null,
  session_id uuid,
  ts timestamptz not null,
  received_at timestamptz not null default now(),
  name text not null,
  app_version text,
  props jsonb not null default '{}'
);
create index if not exists events_name_ts on gmide.events (name, ts);

create table if not exists gmide.builds (
  id bigint generated always as identity primary key,
  install_id uuid not null,
  session_id uuid,
  ts timestamptz not null,
  received_at timestamptz not null default now(),
  target text not null,                  -- run_gx | run_android | apk | web | windows | other
  result text not null,                  -- ok | failed | gml_error | tool_crash | killed | cancelled
  duration_ms int,
  exit_code int,
  runtime_version text,
  app_version text,
  error_signature text,
  error_text text
);
create index if not exists builds_ts on gmide.builds (ts);

create table if not exists gmide.crash_groups (
  signature text primary key,
  process text not null,                 -- ide | build | game | runner
  kind text not null,                    -- managed | java | native | anr | oom | runner_js
  title text not null,
  first_seen timestamptz not null default now(),
  last_seen timestamptz not null default now(),
  occurrences int not null default 0,
  first_version text,
  last_version text,
  status text not null default 'open',   -- open | fixed | ignored
  fixed_in text,
  note text
);

create table if not exists gmide.crashes (
  id bigint generated always as identity primary key,
  install_id uuid not null,
  session_id uuid,
  ts timestamptz not null,
  received_at timestamptz not null default now(),
  signature text not null references gmide.crash_groups(signature),
  process text not null,
  kind text not null,
  exception_type text,
  message text,
  stack text,
  log_tail text,
  app_version text,
  runtime_version text,
  device_model text,
  android_api int,
  meta jsonb not null default '{}'
);
create index if not exists crashes_sig_ts on gmide.crashes (signature, ts);
create index if not exists crashes_ts on gmide.crashes (ts);

-- Resumo diario usado pelo painel.
create or replace view gmide.daily as
with d as (
  select date_trunc('day', started_at)::date as day,
         count(*) as sessions,
         count(distinct install_id) as installs,
         count(*) filter (where end_reason = 'crash') as crashed_sessions,
         percentile_cont(0.5) within group (order by boot_ms) as boot_p50
  from gmide.sessions group by 1
), c as (
  select date_trunc('day', ts)::date as day, count(*) as crashes from gmide.crashes group by 1
), b as (
  select date_trunc('day', ts)::date as day, count(*) as builds,
         count(*) filter (where result = 'ok') as builds_ok
  from gmide.builds group by 1
)
select coalesce(d.day, c.day, b.day) as day,
       coalesce(d.sessions, 0) as sessions, coalesce(d.installs, 0) as installs,
       coalesce(d.crashed_sessions, 0) as crashed_sessions, coalesce(c.crashes, 0) as crashes,
       coalesce(b.builds, 0) as builds, coalesce(b.builds_ok, 0) as builds_ok, d.boot_p50
from d full join c on c.day = d.day full join b on b.day = coalesce(d.day, c.day);
