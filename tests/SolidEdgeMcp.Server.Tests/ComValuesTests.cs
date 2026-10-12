using SolidEdgeMcp.SolidEdge.Com;

namespace SolidEdgeMcp.Server.Tests;

/// <summary>Which parameters of the generic layer receive a path (and so go through the path policy).</summary>
public sealed class ComValuesTests
{
    [Theory]
    [InlineData("SaveAs", "NewName")]
    [InlineData("Add", "TemplateFileName")]
    [InlineData("AddWithTransform", "OccurrenceFileName")]
    [InlineData("FindWhereUsedDocuments", "varFolderPath")]
    [InlineData("TextureFileName", "TextureFileName")]
    [InlineData("ChangeSource", "NewFileName")]
    public void File_and_folder_parameters_are_paths(string member, string parameter) =>
        Assert.True(ComValues.IsFileMember(member, parameter));

    [Theory]
    [InlineData("SearchDocuments", "file_types")]
    [InlineData("FindWhereUsedDocuments", "varFileFilterOrText")]
    [InlineData("SaveAs", "FileType")]
    [InlineData("GetFilterString", "FileFilter")]
    [InlineData("Export", "FileExtension")]
    [InlineData("Open", "Name2")]
    [InlineData("AddByTwoPoints", "x1")]
    public void Types_filters_and_other_parameters_are_not_paths(string member, string parameter) =>
        Assert.False(ComValues.IsFileMember(member, parameter));
}
