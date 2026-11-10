// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeMcp.SolidEdge.Com;

namespace SolidEdgeMcp.Server.Tests;

public sealed class LateTests
{
    [Fact]
    public void TryDelete_deletes_and_accepts_nothing()
    {
        var target = new Deletable(null);

        Late.TryDelete(target);
        Late.TryDelete(null);

        Assert.True(target.Deleted);
    }

    [Theory]
    [InlineData(ComErrors.EFail)]
    [InlineData(ComErrors.CoEObjNotConnected)]
    public void TryDelete_leaves_what_Solid_Edge_refuses_to_delete(int hresult) =>
        Late.TryDelete(new Deletable(new COMException("refused", hresult)));

    [Fact]
    public void TryDelete_leaves_an_object_without_Delete() => Late.TryDelete(new object());

    [Theory]
    [InlineData(ComErrors.RpcSServerUnavailable)]
    [InlineData(ComErrors.RpcEDisconnected)]
    public void TryDelete_raises_Solid_Edge_being_gone(int hresult)
    {
        Exception error = Assert.ThrowsAny<Exception>(() => Late.TryDelete(new Deletable(new COMException("gone", hresult))));

        Assert.True(ComErrors.IsGone(error));
    }

    public sealed class Deletable(Exception? failure)
    {
        public bool Deleted { get; private set; }

        public void Delete()
        {
            if (failure is not null)
            {
                throw failure;
            }

            Deleted = true;
        }
    }
}
