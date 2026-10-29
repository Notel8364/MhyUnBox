using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MhyunBox.Pages;

public sealed class StatusRowSelector : DataTemplateSelector
{
	public DataTemplate? HeaderTemplate { get; set; }

	public DataTemplate? RoleTemplate { get; set; }

	protected override DataTemplate SelectTemplateCore(object item)
	{
		return Select(item);
	}

	protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
	{
		return Select(item);
	}

	private DataTemplate Select(object item)
	{
		if (!(item is StatusRowItem { IsHeader: not false }))
		{
			return RoleTemplate;
		}
		return HeaderTemplate;
	}
}
