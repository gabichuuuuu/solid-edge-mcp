// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// Retries the calls that Solid Edge rejects while it is busy
/// (<c>SERVERCALL_RETRYLATER</c>) instead of failing with <c>RPC_E_CALL_REJECTED</c>.
/// </summary>
internal sealed class OleMessageFilter(int retryWindowMilliseconds) : IOleMessageFilter
{
    private const int ServerCallIsHandled = 0;
    private const int ServerCallRejected = 1;
    private const int ServerCallRetryLater = 2;
    private const int PendingMsgWaitDefProcess = 2;
    private const int RetryDelayMilliseconds = 100;
    private const int CancelCall = -1;

    public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) =>
        ServerCallIsHandled;

    public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
    {
        if ((dwRejectType == ServerCallRetryLater || dwRejectType == ServerCallRejected)
            && dwTickCount < retryWindowMilliseconds)
        {
            return RetryDelayMilliseconds;
        }

        return CancelCall;
    }

    public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => PendingMsgWaitDefProcess;
}
