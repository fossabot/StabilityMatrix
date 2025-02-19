using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using FluentAvalonia.UI.Controls;
using StabilityMatrix.Avalonia.ViewModels.Base; 
using StabilityMatrix.Core.Attributes;

namespace StabilityMatrix.Avalonia;

public class ViewLocator : IDataTemplate, INavigationPageFactory
{
    /// <inheritdoc />
    public Control Build(object? data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));

        var type = data.GetType();
        
        if (Attribute.GetCustomAttribute(type, typeof(ViewAttribute)) is ViewAttribute viewAttr)
        {
            var viewType = viewAttr.GetViewType();
            return GetView(viewType);
        }

        return new TextBlock
        {
            Text = "View Model Not Found: " + data.GetType().FullName
        };
    }

    private Control GetView(Type viewType)
    {
        // Otherwise get from the service provider
        if (App.Services.GetService(viewType) is Control view)
        {
            return view;
        }
        
        return new TextBlock
        {
            Text = "View Not Found: " + viewType.FullName
        };
    }
    
    /// <inheritdoc />
    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }

    /// <inheritdoc />
    public Control? GetPage(Type srcType)
    {
        if (Attribute.GetCustomAttribute(srcType, typeof(ViewAttribute)) is not ViewAttribute
            viewAttr)
        {
            throw new InvalidOperationException("View not found for " + srcType.FullName);
        }

        var viewType = viewAttr.GetViewType();
        var view = GetView(viewType);
        view.DataContext ??= App.Services.GetService(srcType);
        return view;
    }

    /// <inheritdoc />
    public Control GetPageFromObject(object target)
    {
        if (Attribute.GetCustomAttribute(target.GetType(), typeof(ViewAttribute)) is not
            ViewAttribute viewAttr)
        {
            throw new InvalidOperationException("View not found for " + target.GetType().FullName);
        }

        var viewType = viewAttr.GetViewType();
        var view = GetView(viewType);
        view.DataContext ??= target;
        return view;
    }
}
