# No-listener telemetry allocation probe

The macOS unit job for historical net10 head 0626bdbc8922138dffbda385e10179466855cf41
(run 20261015-183642-0626bdbc8922) failed NoListeners_RequestAndOperationChecksAllocateNothing:
expected zero bytes, observed 2,256. Linux and Windows passed. Original output is retained.

The old measurement included Assert.Null inside every measured iteration and only warmed
Request once, leaving BeginLoad unwarmed. The revised fixture creates its provider and warms
both operations 1,000 times on an owned thread without inherited execution context. A synchronous
NoInlining helper measures the same 10,000 request/operation pairs, without assertion/setup or
completion code in the measured region. A non-null operation still fails explicitly. The one
batch must allocate exactly zero bytes; there is no tolerance, minimum or additional batch.
This repair does not establish the source of the original 2,256 bytes.

Temporary production mutations add an escaping 128-byte buffer to Request or BeginLoad separately.
Both fail the revised test with 1,520,000 bytes. Production telemetry is restored byte for byte.

The complete final Core suite passes 1,035 facts, zero failures and one intentional opt-in soak
skip under the default, tr-TR and sv-SE cultures on macOS ARM64/net10. The duplicate-ID warning
also disappeared after moving the already-present AppendNullPart comparison out of a theory:
its computed argument pair was identical to the nullable-string row, so xUnit skipped its
separate assertion. Both paths now have distinct tests; no input case was removed.

This is test-scaffolding verification in the final acceptance PR, not a production behavior
change, a complete latest-head CI run or a performance pass. Other native suites and final
contract/hash performance gates remain separate evidence.
