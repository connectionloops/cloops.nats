# Changelog

Every change to cloops.nats gets an entry here, newest first. Each row in the index links to the detailed section below it. See [AGENTS.md](AGENTS.md#changelog-rule) for the format and a template.

| Date | Change |
| ---- | ------ |
| 2026-10-11 | [Upgrade to NATS.Net 3.3.0 (v2.0.0)](#2026-10-11--upgrade-to-natsnet-330-v200) |
| 2026-10-11 | [Configurable client timeouts, logger factory and NatsOpts hook (v1.4.13)](#2026-10-11--configurable-client-timeouts-logger-factory-and-natsopts-hook-v1413) |
| 2026-09-13 | [Payload validation failures reach the exception handlers (v1.3.12)](#2026-09-13--payload-validation-failures-reach-the-exception-handlers-v1312) |
| 2026-09-06 | [NuGet Trusted Publishing and Node 24 actions (v1.2.11)](#2026-09-06--nuget-trusted-publishing-and-node-24-actions-v1211) |
| 2026-09-06 | [Runtime (dynamic) consumer registration (v1.2.11)](#2026-09-06--runtime-dynamic-consumer-registration-v1211) |
| 2026-07-26 | [64-bit integers serialized as JSON strings (v1.1.9)](#2026-07-26--64-bit-integers-serialized-as-json-strings-v119) |
| 2026-07-25 | [Consumer exception handlers (v1.1.8)](#2026-07-25--consumer-exception-handlers-v118) |
| 2026-07-08 | [Consumer interceptors (v1.1.7)](#2026-07-08--consumer-interceptors-v117) |
| 2025-12-14 | [Schema example and GitHub package metadata (v1.1.6)](#2025-12-14--schema-example-and-github-package-metadata-v116) |
| 2025-12-14 | [Automatic message validation (v1.1.4, v1.1.5)](#2025-12-14--automatic-message-validation-v114-v115) |

## 2026-10-11 — Upgrade to NATS.Net 3.3.0 (v2.0.0)

- **What:** `NATS.Net` 2.6.6 -> 3.3.0, so every `NATS.Client.*` package resolves to 3.3.0. `NATS.Jwt` 1.0.0-preview.16 -> 1.0.1 (stable, same `NATS.NKeys` 1.0.1 as Core 3.3.0). PR #7.
- **Why:** services that also referenced a NATS.Net 3.x package (CloudPathology via `NATS.Client.OpenTelemetry` 3.0.1) resolved Core 3.x under JetStream 2.6.6. That graph is binary incompatible: ordered consumers threw `MissingMethodException` (`NatsOptsExtensions.BackoffWithJitterAsync`).
- **Code:** JetStream `ConsumeAsync` yields `INatsJSMsg<T>` instead of `NatsJSMsg<T>` in 3.x; the internal durable consumer pipeline was updated. No cloops.nats public signature changed.
- **Behaviour:** the client still sets `SubPendingChannelFullMode = Wait`. Direct request-reply and a 16384 pending capacity are now the NATS.Net defaults.
- **Breaking for consumers:** code using the exposed `Connection` / `JsContext` / `KvContext` gets NATS.Net 3.x semantics: `INatsJSMsg<T>` from `ConsumeAsync` / `FetchAsync` / `NextAsync` (code using `var` is unaffected), and new members on `INatsJSContext` / `INatsJSStream` / `INatsJSConsumer` for hand-written implementations.
- **Consumers must:** move any directly referenced `NATS.Client.*` package to 3.x (preferably 3.3.0). Libraries compiled against cloops.nats 1.x plus NATS.Net 2.6 that call NATS APIs directly should be rebuilt.
- **Tests:** new `JetStreamIntegrationTests` against a real `nats:2.11.7-alpine -js` (ordered consumer, filtered consumer create, SDK durable consumer end to end). CI starts the server; locally they skip unless `NATS_TEST_URL` is set.
- **Versioning:** the major bump is wired through CI (`MAJOR: 2`, `MINOR: 0`). The patch is now the next free `v{MAJOR}.{MINOR}.*` tag instead of the run number, so this release is `2.0.0`. Runs are serialized, and the publish job reuses the build job's version.
- **Cleanup:** removed the unused `azure-pipelines.yml`, which still hard-coded `major: 1` and dated from before the move to GitHub. The csproj's local default `<Version>` is now `2.0.0`.
- **Process:** added this changelog and `AGENTS.md`, which requires a changelog entry for every change.

## 2026-10-11 — Configurable client timeouts, logger factory and NatsOpts hook (v1.4.13)

- **What:** new `CloopsNatsClientOptions` with `RequestTimeout`, `CommandTimeout`, `ConnectTimeout`, `LoggerFactory`, `LogConnectionEvents` and a `Configure` hook (`Func<NatsOpts, NatsOpts?>`) applied last. PR #6.
- **API:** new `CloopsNatsClient(url, name, creds, options)` overload. The three-argument constructor is untouched, so already-compiled callers keep working. The effective options are exposed as `CloopsNatsClient.Options`.
- **Why:** callers could not change NATS.Net's 5 s `RequestTimeout`, which also bounds JetStream publish ack waits. Under thread-pool starvation, services surfaced `NatsJSPublishNoResponseException` although the server had replied.
- **Logging:** a supplied `LoggerFactory` is handed to NATS.Net (it was a `NullLoggerFactory`, so its reconnect, ping and retry diagnostics were invisible). Connection lifecycle events are logged with the server name.
- **Robustness:** lifecycle log handlers run best-effort, so a broken logger cannot stop the NATS.Net event pump. A `LoggerFactory` supplied through `Configure` is honoured the same way as one set directly.
- **Validation:** zero, negative or out-of-range timeouts are rejected at construction; `Timeout.InfiniteTimeSpan` is allowed.
- **Impact:** opt-in only. Unset properties keep the NATS.Net defaults, so existing callers behave exactly as before.
- **Version:** CI `MINOR` 3 -> 4; published as 1.4.13.

## 2026-09-13 — Payload validation failures reach the exception handlers (v1.3.12)

- **What:** a failed `Validate()` is now offered to registered `INatsConsumerExceptionHandler`s exactly like a handler fault. PR #5.
- **Why:** validation ran on the subscription loop, before the work item, so exception handlers never saw it. Core request/reply callers got a timeout instead of a typed error.
- **How:** validation moved into the work item (`NatsConsumerHandlerInvoker.InvokeWithValidationAsync`). The reflection caches moved to `NatsMessageValidator` as `ConcurrentDictionary`, because validation now runs on the worker pool.
- **API:** `NatsConsumerExceptionContext.IsValidationFailure` is new, added as a constructor overload rather than an optional parameter so it stays binary compatible.
- **Exception type:** handlers receive the exception the schema threw, not an SDK wrapper, so existing pattern matching on validation exceptions keeps working.
- **JetStream:** a validation failure is never redelivered. An exception handler asking for redelivery is downgraded to a terminate and logged at `Warning`.
- **Impact:** with no exception handler registered, behaviour is unchanged (JetStream terminates, Core drops), except that the discard now records a fail metric.
- **Version:** published as 1.3.12.

## 2026-09-06 — NuGet Trusted Publishing and Node 24 actions (v1.2.11)

- **What:** the publish job exchanges its GitHub OIDC token for a short-lived NuGet key via `NuGet/login@v1` and runs in the `production` environment, matching the Trusted Publishing policy on nuget.org.
- **Why:** the stored `NUGET_API_KEY` had expired and the 1.2.10 publish failed with 403. There is now no stored secret to rotate.
- **Also:** bumped `actions/checkout`, `setup-dotnet` and `upload-artifact` to v5 and `github-script` to v8, clearing the Node 20 deprecation warnings.
- **Docs:** `docs/ReleaseProcess.md` describes the Trusted Publishing policy (repo, workflow file, environment).
- **Impact:** no library change. This run published 1.2.11, which first shipped the runtime consumer registration below.

## 2026-09-06 — Runtime (dynamic) consumer registration (v1.2.11)

- **What:** `NatsDynamicConsumer` binds consumers whose subjects and durable ids are only known at runtime; `INatsDynamicConsumerSource` with `AddNatsDynamicConsumerSource<T>()` discovers them when consumers are mapped. PR #4.
- **Why:** `[NatsConsumer]` is not `AllowMultiple` and takes compile-time constants, so "one handler, N durable consumers" (for example re-sharded lanes) was not expressible.
- **API:** a new `MapConsumers` overload takes the runtime consumer list. The four-argument overload is untouched, keeping cloops.microservices 1.1.25 binary compatible. `ICloopsNatsClient` gets a default implementation for existing implementers.
- **Discovery budget:** each source gets 30 s (`NATS_DYNAMIC_CONSUMER_DISCOVERY_TIMEOUT_SECONDS`, `0` disables), enforced even if the source ignores cancellation. On expiry it logs `Critical` and throws `TimeoutException`.
- **Bug fix:** the consumer id -> processor map is built once across all assemblies. A consumer id declared in two assemblies previously produced two processors attached to the same JetStream consumer.
- **Validation:** the handler signature is checked in the `NatsDynamicConsumer` constructor. A `QueueGroupName` on a durable consumer is now rejected instead of silently ignored.
- **Default change:** `NATS_SUBSCRIPTION_QUEUE_SIZE` now defaults to 2 x MaxDOP (256) instead of 20,000, so queued messages do not outlive `AckWait` and get redelivered while still in memory.
- **Default change:** JetStream ack, nak and terminate use `DoubleAck` by default (`NATS_CONSUMER_DOUBLE_ACK=false` restores fire-and-forget). A handler's explicit `AckOpts.DoubleAck` wins.
- **CI:** 15-minute job timeout. `MINOR` 1 -> 2. The 1.2.10 publish failed (expired key), so this first shipped in 1.2.11.

## 2026-07-26 — 64-bit integers serialized as JSON strings (v1.1.9)

- **What:** new public `Int64StringJsonConverter` and `UInt64StringJsonConverter`, registered in the SDK's shared `JsonSerializerOptions`.
- **Effect:** 64-bit values stay exact for JSON readers that parse numbers as doubles (JavaScript loses precision above 2^53). The commit (`handle long and ulong`) gives no further rationale.
- **Reading:** both converters accept string and numeric tokens, so payloads from older producers still deserialize.
- **Impact (wire format):** `long` and `ulong` fields are now written as JSON strings. Consumers on an SDK older than 1.1.9, or non-SDK consumers expecting numbers, must accept string-encoded integers.
- **Tests:** `LongJsonConvertersTests` cover string output, string and numeric input, and `MinValue` / `MaxValue` boundaries.
- **Version:** published as 1.1.9.

## 2026-07-25 — Consumer exception handlers (v1.1.8)

- **What:** `INatsConsumerExceptionHandler`, registered with `AddNatsConsumerExceptionHandler<T>()`, maps a handler exception to a `NatsAck` (ack, nak, terminate or Core reply). Handlers run in registration order and the first non-null result wins. PR #3.
- **Observability:** mapped exceptions are logged at `Warning`. The resulting `NatsAck` carries `IsExceptionMapped`, `MappedException` and `MappedByHandlerType`, and the invocation still counts as a failure in metrics.
- **Isolation:** an exception handler that throws is logged at `Error` and the next one runs. `OperationCanceledException` during shutdown is never converted to an ack.
- **Behaviour change:** an unhandled handler exception now leaves the JetStream message unacked (redelivery follows `AckWait` / `MaxDeliver`) instead of an immediate NAK.
- **Unmatched subjects:** a delivered subject with no handler is logged and left unacked for the stream's DLQ policy instead of faulting the listener loop.
- **Context:** `NatsConsumerInterceptorContext` and `NatsConsumerExceptionContext` gained public constructors for unit testing, plus `IsJetStream` and a read-only `JetStream` delivery view (`NatsConsumerJetStreamMetadata`).
- **Internals:** added `NatsConsumerHandlerInvoker`, `NatsConsumerInvocationPlan` and `NatsMsgAccessor` (compiled per-type message factories instead of per-message reflection).
- **Tests and CI:** added the `cloops.nats.sdk.Tests` project. CI runs it without `continue-on-error`, so failing tests now fail the build.
- **Version:** published as 1.1.8.

## 2026-07-08 — Consumer interceptors (v1.1.7)

- **What:** `INatsConsumerInterceptor` runs before a consumer handler and can inspect, replace or reject the message. Interceptors are resolved from DI and run in registration order. PR #2.
- **Results:** `NatsConsumerInterceptorResult.Continue()` or `Reject(reason, shouldRetryDelivery, reply)`. A rejected JetStream message is NAKed when `shouldRetryDelivery` is true and terminated otherwise. A rejected Core request can be answered with `reply`.
- **Context:** `NatsConsumerInterceptorContext` exposes the matched and actual subject, payload type, handler type and method, the typed message and the raw bytes.
- **Message replacement:** `BaseNatsUtil.CreateTypedMsgWrapper` builds a typed `NatsMsg<T>` with a new payload while preserving subject, reply-to, headers and connection.
- **Impact:** opt-in. With no interceptor registered, processing is unchanged.
- **Version:** published as 1.1.7.

## 2025-12-14 — Schema example and GitHub package metadata (v1.1.6)

- **What:** the `examples/schema` folder shows schema-based messaging: a `BaseMessage` with Data Annotations `Validate()`, typed `P_` / `S_` / `R_` subject types, and a `cnc.Subjects().Example()` extension entry point.
- **Docs:** new `examples/schema/README.md` covers one message per subject, validation on publish and consume, and best practices. The README tip now points to it.
- **Examples:** the request example sends a typed `Person` and expects it echoed back. Example IDs use `SequentialGuid`.
- **Package metadata:** `RepositoryUrl` and `PackageProjectUrl` now point to GitHub instead of Azure DevOps, and the package description was updated.
- **Impact:** no library behaviour change.
- **Version:** published as 1.1.6.

## 2025-12-14 — Automatic message validation (v1.1.4, v1.1.5)

- **What:** if a consumer's payload type has a public parameterless `Validate()` method, the SDK calls it before the handler runs.
- **Failure handling:** an invalid JetStream message is terminated (`AckTerminate`, never retried); an invalid Core message is dropped. Both are logged at `Error`, and the handler is never invoked.
- **Performance:** the `Validate()` method and the message `Data` property are looked up once per type and cached.
- **Impact:** payload types without a `Validate()` method are unaffected. Types that have one now reject invalid messages before dispatch.
- **Docs:** README and `CurrentFunctionality.md` describe the feature. A same-day README follow-up (0b52335, v1.1.5) clarifies that invalid messages never reach handlers.
- **Version:** shipped in 1.1.4; the README clarification in 1.1.5.
