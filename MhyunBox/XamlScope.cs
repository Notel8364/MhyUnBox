using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MhyunBox;

/// <summary>
/// XAML 名称绑定器：当无法使用 XAML 编译器生成的连接代码时，
/// 在 LoadComponent 之后按元素名称回填字段并挂接事件处理器。
/// </summary>
internal static class XamlScope
{
	private const int MaxDepth = 32;

	public static void Bind(object root)
	{
		if (root == null)
		{
			return;
		}
		Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
		Collect(root, map, 0);
		if (map.Count == 0)
		{
			return;
		}
		Type type = root.GetType();
		int bound = 0;
		while (type != null)
		{
			FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
			foreach (FieldInfo field in fields)
			{
				if (map.TryGetValue(field.Name, out object value) && field.FieldType.IsInstanceOfType(value))
				{
					field.SetValue(root, value);
					bound++;
				}
			}
			type = type.BaseType;
		}
	}

	public static object Find(object root, string name)
	{
		if (root == null)
		{
			return null;
		}
		Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
		Collect(root, map, 0);
		map.TryGetValue(name, out object value);
		return value;
	}

	/// <summary>按显示文本查找元素（用于反编译后丢失 x:Name 的按钮，如“查看关于”）。</summary>
	public static object FindByContent(object root, string content)
	{
		if (root == null || string.IsNullOrEmpty(content))
		{
			return null;
		}
		Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
		Collect(root, map, 0);
		foreach (object value in map.Values)
		{
			if (value is ContentControl cc && cc.Content is string text && text == content)
			{
				return value;
			}
		}
		return null;
	}

	/// <summary>按名称挂接事件（幂等：先摘后挂）。</summary>
	public static void Wire(object target, string eventName, object owner, string methodName)
	{
		if (target == null || owner == null)
		{
			return;
		}
		EventInfo ei = target.GetType().GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
		if (ei == null)
		{
			return;
		}
		MethodInfo mi = owner.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
		if (mi == null)
		{
			return;
		}
		Delegate handler;
		try
		{
			handler = Delegate.CreateDelegate(ei.EventHandlerType, owner, mi);
		}
		catch
		{
			return;
		}
		try
		{
			ei.RemoveEventHandler(target, handler);
			ei.AddEventHandler(target, handler);
		}
		catch
		{
		}
	}

	private static void Collect(object o, Dictionary<string, object> map, int depth)
	{
		if (o == null || depth > MaxDepth)
		{
			return;
		}
		if (o is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name) && !map.ContainsKey(fe.Name))
		{
			map[fe.Name] = o;
		}
		switch (o)
		{
		case Panel panel:
			foreach (object child in panel.Children)
			{
				Collect(child, map, depth + 1);
			}
			break;
		case Border border:
			Collect(border.Child, map, depth + 1);
			break;
		case ScrollViewer scrollViewer:
			Collect(scrollViewer.Content, map, depth + 1);
			break;
		case ContentControl contentControl:
			Collect(contentControl.Content, map, depth + 1);
			break;
		case Page page:
			Collect(page.Content, map, depth + 1);
			break;
		case UserControl userControl:
			Collect(userControl.Content, map, depth + 1);
			break;
		case Window window:
			Collect(window.Content, map, depth + 1);
			break;
		}
	}
}
