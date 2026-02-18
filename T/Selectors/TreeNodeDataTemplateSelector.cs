using Avalonia.Controls;
using Avalonia.Controls.Templates;
using T.Models;

namespace T.Selectors;

public class TreeNodeDataTemplateSelector : IDataTemplate
{
    public IDataTemplate? FolderTemplate { get; set; }
    public IDataTemplate? SessionTemplate { get; set; }

    public Control? Build(object? param)
    {
        return param switch
        {
            FolderTreeNode => FolderTemplate?.Build(param),
            SessionTreeNode => SessionTemplate?.Build(param),
            _ => null
        };
    }

    public bool Match(object? data)
    {
        return data is FolderTreeNode or SessionTreeNode;
    }
}