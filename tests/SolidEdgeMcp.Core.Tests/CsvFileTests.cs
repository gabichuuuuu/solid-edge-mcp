// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Text;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class CsvFileTests
{
    [Fact]
    public void Quotes_only_the_values_that_need_it()
    {
        string path = Path.Combine(Path.GetTempPath(), $"csv-{Guid.NewGuid():N}.csv");
        try
        {
            CsvFile.Write(path, [["Item", "Name", "Quantity"], ["1", "Hoja, acero", "2"], ["2", "Eje \"5\"", null], ["3", "two\nlines", "1"]]);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.Equal("Item,Name,Quantity\r\n1,\"Hoja, acero\",2\r\n2,\"Eje \"\"5\"\"\",\r\n3,\"two\nlines\",1\r\n", Encoding.UTF8.GetString(bytes[3..]));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
