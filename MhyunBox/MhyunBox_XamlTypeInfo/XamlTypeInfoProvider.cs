using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MhyunBox.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace MhyunBox.MhyunBox_XamlTypeInfo;

[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2509")]
[DebuggerNonUserCode]
internal class XamlTypeInfoProvider
{
	private Dictionary<string, IXamlType> _xamlTypeCacheByName = new Dictionary<string, IXamlType>();

	private Dictionary<Type, IXamlType> _xamlTypeCacheByType = new Dictionary<Type, IXamlType>();

	private Dictionary<string, IXamlMember> _xamlMembers = new Dictionary<string, IXamlMember>();

	private string[] _typeNameTable;

	private Type[] _typeTable;

	private List<IXamlMetadataProvider> _otherProviders;

	private List<IXamlMetadataProvider> OtherProviders
	{
		get
		{
			if (_otherProviders == null)
			{
				List<IXamlMetadataProvider> list = new List<IXamlMetadataProvider>();
				IXamlMetadataProvider item = new XamlControlsXamlMetaDataProvider();
				list.Add(item);
				_otherProviders = list;
			}
			return _otherProviders;
		}
	}

	public IXamlType GetXamlTypeByType(Type type)
	{
		IXamlType value;
		lock (_xamlTypeCacheByType)
		{
			if (_xamlTypeCacheByType.TryGetValue(type, out value))
			{
				return value;
			}
			int num = LookupTypeIndexByType(type);
			if (num != -1)
			{
				value = CreateXamlType(num);
			}
			XamlUserType xamlUserType = value as XamlUserType;
			if (value == null || (xamlUserType != null && xamlUserType.IsReturnTypeStub && !xamlUserType.IsLocalType))
			{
				IXamlType xamlType = CheckOtherMetadataProvidersForType(type);
				if (xamlType != null && (xamlType.IsConstructible || value == null))
				{
					value = xamlType;
				}
			}
			if (value != null)
			{
				_xamlTypeCacheByName.Add(value.FullName, value);
				_xamlTypeCacheByType.Add(value.UnderlyingType, value);
			}
		}
		return value;
	}

	public IXamlType GetXamlTypeByName(string typeName)
	{
		if (string.IsNullOrEmpty(typeName))
		{
			return null;
		}
		IXamlType value;
		lock (_xamlTypeCacheByType)
		{
			if (_xamlTypeCacheByName.TryGetValue(typeName, out value))
			{
				return value;
			}
			int num = LookupTypeIndexByName(typeName);
			if (num != -1)
			{
				value = CreateXamlType(num);
			}
			XamlUserType xamlUserType = value as XamlUserType;
			if (value == null || (xamlUserType != null && xamlUserType.IsReturnTypeStub && !xamlUserType.IsLocalType))
			{
				IXamlType xamlType = CheckOtherMetadataProvidersForName(typeName);
				if (xamlType != null && (xamlType.IsConstructible || value == null))
				{
					value = xamlType;
				}
			}
			if (value != null)
			{
				_xamlTypeCacheByName.Add(value.FullName, value);
				_xamlTypeCacheByType.Add(value.UnderlyingType, value);
			}
		}
		return value;
	}

	public IXamlMember GetMemberByLongName(string longMemberName)
	{
		if (string.IsNullOrEmpty(longMemberName))
		{
			return null;
		}
		IXamlMember value;
		lock (_xamlMembers)
		{
			if (_xamlMembers.TryGetValue(longMemberName, out value))
			{
				return value;
			}
			value = CreateXamlMember(longMemberName);
			if (value != null)
			{
				_xamlMembers.Add(longMemberName, value);
			}
		}
		return value;
	}

	private void InitTypeTables()
	{
		_typeNameTable = new string[51];
		_typeNameTable[0] = "Microsoft.UI.Xaml.Controls.XamlControlsResources";
		_typeNameTable[1] = "Microsoft.UI.Xaml.ResourceDictionary";
		_typeNameTable[2] = "Object";
		_typeNameTable[3] = "Boolean";
		_typeNameTable[4] = "Microsoft.UI.Xaml.Controls.NavigationView";
		_typeNameTable[5] = "Microsoft.UI.Xaml.Controls.ContentControl";
		_typeNameTable[6] = "Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode";
		_typeNameTable[7] = "System.Enum";
		_typeNameTable[8] = "System.ValueType";
		_typeNameTable[9] = "Microsoft.UI.Xaml.Controls.NavigationViewBackButtonVisible";
		_typeNameTable[10] = "Double";
		_typeNameTable[11] = "Microsoft.UI.Xaml.Controls.AutoSuggestBox";
		_typeNameTable[12] = "Microsoft.UI.Xaml.UIElement";
		_typeNameTable[13] = "Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode";
		_typeNameTable[14] = "System.Collections.Generic.IList`1<Object>";
		_typeNameTable[15] = "Microsoft.UI.Xaml.DataTemplate";
		_typeNameTable[16] = "Microsoft.UI.Xaml.Style";
		_typeNameTable[17] = "Microsoft.UI.Xaml.Controls.StyleSelector";
		_typeNameTable[18] = "Microsoft.UI.Xaml.Controls.DataTemplateSelector";
		_typeNameTable[19] = "Microsoft.UI.Xaml.Controls.NavigationViewOverflowLabelMode";
		_typeNameTable[20] = "String";
		_typeNameTable[21] = "Microsoft.UI.Xaml.Controls.NavigationViewSelectionFollowsFocus";
		_typeNameTable[22] = "Microsoft.UI.Xaml.Controls.NavigationViewShoulderNavigationEnabled";
		_typeNameTable[23] = "Microsoft.UI.Xaml.Controls.NavigationViewTemplateSettings";
		_typeNameTable[24] = "Microsoft.UI.Xaml.DependencyObject";
		_typeNameTable[25] = "MhyunBox.MainWindow";
		_typeNameTable[26] = "Microsoft.UI.Xaml.Window";
		_typeNameTable[27] = "Microsoft.UI.Xaml.ElementTheme";
		_typeNameTable[28] = "MhyunBox.Pages.AboutPage";
		_typeNameTable[29] = "Microsoft.UI.Xaml.Controls.Page";
		_typeNameTable[30] = "Microsoft.UI.Xaml.Controls.UserControl";
		_typeNameTable[31] = "MhyunBox.Pages.HomePage";
		_typeNameTable[32] = "Microsoft.UI.Xaml.Controls.InfoBar";
		_typeNameTable[33] = "Microsoft.UI.Xaml.Controls.Control";
		_typeNameTable[34] = "Microsoft.UI.Xaml.Controls.Primitives.ButtonBase";
		_typeNameTable[35] = "System.Windows.Input.ICommand";
		_typeNameTable[36] = "Microsoft.UI.Xaml.Controls.IconSource";
		_typeNameTable[37] = "Microsoft.UI.Xaml.Controls.InfoBarSeverity";
		_typeNameTable[38] = "Microsoft.UI.Xaml.Controls.InfoBarTemplateSettings";
		_typeNameTable[39] = "Microsoft.UI.Xaml.Controls.ProgressRing";
		_typeNameTable[40] = "Microsoft.UI.Xaml.Controls.ProgressRingTemplateSettings";
		_typeNameTable[41] = "MhyunBox.Pages.LoginPage";
		_typeNameTable[42] = "MhyunBox.Pages.StatusRowSelector";
		_typeNameTable[43] = "MhyunBox.Pages.ProxyCapturePage";
		_typeNameTable[44] = "Microsoft.UI.Xaml.Controls.ListViewItem";
		_typeNameTable[45] = "MhyunBox.Pages.SettingsPage";
		_typeNameTable[46] = null;
		_typeNameTable[47] = "MhyunBox.Pages.ToolsPage";
		_typeNameTable[48] = "Microsoft.UI.Xaml.Controls.TreeViewNode";
		_typeNameTable[49] = "System.Collections.Generic.IList`1<Microsoft.UI.Xaml.Controls.TreeViewNode>";
		_typeNameTable[50] = "Int32";
		_typeTable = new Type[51];
		_typeTable[0] = typeof(XamlControlsResources);
		_typeTable[1] = typeof(ResourceDictionary);
		_typeTable[2] = typeof(object);
		_typeTable[3] = typeof(bool);
		_typeTable[4] = typeof(NavigationView);
		_typeTable[5] = typeof(ContentControl);
		_typeTable[6] = typeof(NavigationViewPaneDisplayMode);
		_typeTable[7] = typeof(Enum);
		_typeTable[8] = typeof(ValueType);
		_typeTable[9] = typeof(NavigationViewBackButtonVisible);
		_typeTable[10] = typeof(double);
		_typeTable[11] = typeof(AutoSuggestBox);
		_typeTable[12] = typeof(UIElement);
		_typeTable[13] = typeof(NavigationViewDisplayMode);
		_typeTable[14] = typeof(IList<object>);
		_typeTable[15] = typeof(DataTemplate);
		_typeTable[16] = typeof(Style);
		_typeTable[17] = typeof(StyleSelector);
		_typeTable[18] = typeof(DataTemplateSelector);
		_typeTable[19] = typeof(NavigationViewOverflowLabelMode);
		_typeTable[20] = typeof(string);
		_typeTable[21] = typeof(NavigationViewSelectionFollowsFocus);
		_typeTable[22] = typeof(NavigationViewShoulderNavigationEnabled);
		_typeTable[23] = typeof(NavigationViewTemplateSettings);
		_typeTable[24] = typeof(DependencyObject);
		_typeTable[25] = typeof(MainWindow);
		_typeTable[26] = typeof(Window);
		_typeTable[27] = typeof(ElementTheme);
		_typeTable[28] = typeof(AboutPage);
		_typeTable[29] = typeof(Page);
		_typeTable[30] = typeof(UserControl);
		_typeTable[31] = typeof(HomePage);
		_typeTable[32] = typeof(InfoBar);
		_typeTable[33] = typeof(Control);
		_typeTable[34] = typeof(ButtonBase);
		_typeTable[35] = typeof(ICommand);
		_typeTable[36] = typeof(IconSource);
		_typeTable[37] = typeof(InfoBarSeverity);
		_typeTable[38] = typeof(InfoBarTemplateSettings);
		_typeTable[39] = typeof(ProgressRing);
		_typeTable[40] = typeof(ProgressRingTemplateSettings);
		_typeTable[41] = typeof(LoginPage);
		_typeTable[42] = typeof(StatusRowSelector);
		_typeTable[43] = typeof(ProxyCapturePage);
		_typeTable[44] = typeof(ListViewItem);
		_typeTable[45] = typeof(SettingsPage);
		_typeTable[46] = null;
		_typeTable[47] = typeof(ToolsPage);
		_typeTable[48] = typeof(TreeViewNode);
		_typeTable[49] = typeof(IList<TreeViewNode>);
		_typeTable[50] = typeof(int);
	}

	private int LookupTypeIndexByName(string typeName)
	{
		if (_typeNameTable == null)
		{
			InitTypeTables();
		}
		for (int i = 0; i < _typeNameTable.Length; i++)
		{
			if (string.CompareOrdinal(_typeNameTable[i], typeName) == 0)
			{
				return i;
			}
		}
		return -1;
	}

	private int LookupTypeIndexByType(Type type)
	{
		if (_typeTable == null)
		{
			InitTypeTables();
		}
		for (int i = 0; i < _typeTable.Length; i++)
		{
			if (type == _typeTable[i])
			{
				return i;
			}
		}
		return -1;
	}

	private object Activate_0_XamlControlsResources()
	{
		return new XamlControlsResources();
	}

	private object Activate_4_NavigationView()
	{
		return new NavigationView();
	}

	private object Activate_23_NavigationViewTemplateSettings()
	{
		return new NavigationViewTemplateSettings();
	}

	private object Activate_25_MainWindow()
	{
		return new MainWindow();
	}

	private object Activate_28_AboutPage()
	{
		return new AboutPage();
	}

	private object Activate_31_HomePage()
	{
		return new HomePage();
	}

	private object Activate_32_InfoBar()
	{
		return new InfoBar();
	}

	private object Activate_38_InfoBarTemplateSettings()
	{
		return new InfoBarTemplateSettings();
	}

	private object Activate_39_ProgressRing()
	{
		return new ProgressRing();
	}

	private object Activate_41_LoginPage()
	{
		return new LoginPage();
	}

	private object Activate_42_StatusRowSelector()
	{
		return new StatusRowSelector();
	}

	private object Activate_43_ProxyCapturePage()
	{
		return new ProxyCapturePage();
	}

	private object Activate_45_SettingsPage()
	{
		return new SettingsPage();
	}

	private object Activate_47_ToolsPage()
	{
		return new ToolsPage();
	}

	private object Activate_48_TreeViewNode()
	{
		return new TreeViewNode();
	}

	private void StaticInitializer_0_XamlControlsResources()
	{
		RuntimeHelpers.RunClassConstructor(typeof(XamlControlsResources).TypeHandle);
	}

	private void StaticInitializer_4_NavigationView()
	{
		RuntimeHelpers.RunClassConstructor(typeof(NavigationView).TypeHandle);
	}

	private void StaticInitializer_6_NavigationViewPaneDisplayMode()
	{
		RuntimeHelpers.RunClassConstructor(typeof(NavigationViewPaneDisplayMode).TypeHandle);
	}

	private void StaticInitializer_7_Enum()
	{
		RuntimeHelpers.RunClassConstructor(typeof(Enum).TypeHandle);
	}

	private void StaticInitializer_8_ValueType()
	{
		RuntimeHelpers.RunClassConstructor(typeof(ValueType).TypeHandle);
	}

	private void StaticInitializer_9_NavigationViewBackButtonVisible()
	{
		RuntimeHelpers.RunClassConstructor(typeof(NavigationViewBackButtonVisible).TypeHandle);
	}

	private void StaticInitializer_13_NavigationViewDisplayMode()
	{
		RuntimeHelpers.RunClassConstructor(typeof(NavigationViewDisplayMode).TypeHandle);
	}

	private void StaticInitializer_14_IList()
	{
		RuntimeHelpers.RunClassConstructor(typeof(IList<object>).TypeHandle);
	}

	private void StaticInitializer_19_NavigationViewOverflowLabelMode()
	{
		RuntimeHelpers.RunClassConstructor(typeof(NavigationViewOverflowLabelMode).TypeHandle);
	}

	private void StaticInitializer_21_NavigationViewSelectionFollowsFocus()
	{
		RuntimeHelpers.RunClassConstructor(typeof(NavigationViewSelectionFollowsFocus).TypeHandle);
	}

	private void StaticInitializer_22_NavigationViewShoulderNavigationEnabled()
	{
		RuntimeHelpers.RunClassConstructor(typeof(NavigationViewShoulderNavigationEnabled).TypeHandle);
	}

	private void StaticInitializer_23_NavigationViewTemplateSettings()
	{
		RuntimeHelpers.RunClassConstructor(typeof(NavigationViewTemplateSettings).TypeHandle);
	}

	private void StaticInitializer_25_MainWindow()
	{
		RuntimeHelpers.RunClassConstructor(typeof(MainWindow).TypeHandle);
	}

	private void StaticInitializer_28_AboutPage()
	{
		RuntimeHelpers.RunClassConstructor(typeof(AboutPage).TypeHandle);
	}

	private void StaticInitializer_31_HomePage()
	{
		RuntimeHelpers.RunClassConstructor(typeof(HomePage).TypeHandle);
	}

	private void StaticInitializer_32_InfoBar()
	{
		RuntimeHelpers.RunClassConstructor(typeof(InfoBar).TypeHandle);
	}

	private void StaticInitializer_35_ICommand()
	{
		RuntimeHelpers.RunClassConstructor(typeof(ICommand).TypeHandle);
	}

	private void StaticInitializer_37_InfoBarSeverity()
	{
		RuntimeHelpers.RunClassConstructor(typeof(InfoBarSeverity).TypeHandle);
	}

	private void StaticInitializer_38_InfoBarTemplateSettings()
	{
		RuntimeHelpers.RunClassConstructor(typeof(InfoBarTemplateSettings).TypeHandle);
	}

	private void StaticInitializer_39_ProgressRing()
	{
		RuntimeHelpers.RunClassConstructor(typeof(ProgressRing).TypeHandle);
	}

	private void StaticInitializer_40_ProgressRingTemplateSettings()
	{
		RuntimeHelpers.RunClassConstructor(typeof(ProgressRingTemplateSettings).TypeHandle);
	}

	private void StaticInitializer_41_LoginPage()
	{
		RuntimeHelpers.RunClassConstructor(typeof(LoginPage).TypeHandle);
	}

	private void StaticInitializer_42_StatusRowSelector()
	{
		RuntimeHelpers.RunClassConstructor(typeof(StatusRowSelector).TypeHandle);
	}

	private void StaticInitializer_43_ProxyCapturePage()
	{
		RuntimeHelpers.RunClassConstructor(typeof(ProxyCapturePage).TypeHandle);
	}

	private void StaticInitializer_45_SettingsPage()
	{
		RuntimeHelpers.RunClassConstructor(typeof(SettingsPage).TypeHandle);
	}

	private void StaticInitializer_47_ToolsPage()
	{
		RuntimeHelpers.RunClassConstructor(typeof(ToolsPage).TypeHandle);
	}

	private void StaticInitializer_48_TreeViewNode()
	{
		RuntimeHelpers.RunClassConstructor(typeof(TreeViewNode).TypeHandle);
	}

	private void StaticInitializer_49_IList()
	{
		RuntimeHelpers.RunClassConstructor(typeof(IList<TreeViewNode>).TypeHandle);
	}

	private void MapAdd_0_XamlControlsResources(object instance, object key, object item)
	{
		((IDictionary<object, object>)instance).Add(key, item);
	}

	private void VectorAdd_14_IList(object instance, object item)
	{
		((ICollection<object>)instance).Add(item);
	}

	private void VectorAdd_49_IList(object instance, object item)
	{
		ICollection<TreeViewNode> collection = (ICollection<TreeViewNode>)instance;
		TreeViewNode item2 = (TreeViewNode)item;
		collection.Add(item2);
	}

	private IXamlType CreateXamlType(int typeIndex)
	{
		XamlSystemBaseType result = null;
		string fullName = _typeNameTable[typeIndex];
		Type type = _typeTable[typeIndex];
		switch (typeIndex)
		{
		case 0:
		{
			XamlUserType xamlUserType27 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.ResourceDictionary"));
			xamlUserType27.Activator = Activate_0_XamlControlsResources;
			xamlUserType27.StaticInitializer = StaticInitializer_0_XamlControlsResources;
			xamlUserType27.DictionaryAdd = MapAdd_0_XamlControlsResources;
			xamlUserType27.AddMemberName("UseCompactResources");
			result = xamlUserType27;
			break;
		}
		case 1:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 2:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 3:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 4:
		{
			XamlUserType xamlUserType26 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.Controls.ContentControl"));
			xamlUserType26.Activator = Activate_4_NavigationView;
			xamlUserType26.StaticInitializer = StaticInitializer_4_NavigationView;
			xamlUserType26.AddMemberName("PaneDisplayMode");
			xamlUserType26.AddMemberName("IsPaneToggleButtonVisible");
			xamlUserType26.AddMemberName("IsBackButtonVisible");
			xamlUserType26.AddMemberName("IsSettingsVisible");
			xamlUserType26.AddMemberName("AlwaysShowHeader");
			xamlUserType26.AddMemberName("OpenPaneLength");
			xamlUserType26.AddMemberName("AutoSuggestBox");
			xamlUserType26.AddMemberName("CompactModeThresholdWidth");
			xamlUserType26.AddMemberName("CompactPaneLength");
			xamlUserType26.AddMemberName("ContentOverlay");
			xamlUserType26.AddMemberName("DisplayMode");
			xamlUserType26.AddMemberName("ExpandedModeThresholdWidth");
			xamlUserType26.AddMemberName("FooterMenuItems");
			xamlUserType26.AddMemberName("FooterMenuItemsSource");
			xamlUserType26.AddMemberName("Header");
			xamlUserType26.AddMemberName("HeaderTemplate");
			xamlUserType26.AddMemberName("IsBackEnabled");
			xamlUserType26.AddMemberName("IsPaneOpen");
			xamlUserType26.AddMemberName("IsPaneVisible");
			xamlUserType26.AddMemberName("IsTitleBarAutoPaddingEnabled");
			xamlUserType26.AddMemberName("MenuItemContainerStyle");
			xamlUserType26.AddMemberName("MenuItemContainerStyleSelector");
			xamlUserType26.AddMemberName("MenuItemTemplate");
			xamlUserType26.AddMemberName("MenuItemTemplateSelector");
			xamlUserType26.AddMemberName("MenuItems");
			xamlUserType26.AddMemberName("MenuItemsSource");
			xamlUserType26.AddMemberName("OverflowLabelMode");
			xamlUserType26.AddMemberName("PaneCustomContent");
			xamlUserType26.AddMemberName("PaneFooter");
			xamlUserType26.AddMemberName("PaneHeader");
			xamlUserType26.AddMemberName("PaneTitle");
			xamlUserType26.AddMemberName("PaneToggleButtonStyle");
			xamlUserType26.AddMemberName("SelectedItem");
			xamlUserType26.AddMemberName("SelectionFollowsFocus");
			xamlUserType26.AddMemberName("SettingsItem");
			xamlUserType26.AddMemberName("ShoulderNavigationEnabled");
			xamlUserType26.AddMemberName("TemplateSettings");
			result = xamlUserType26;
			break;
		}
		case 5:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 6:
		{
			XamlUserType xamlUserType25 = new XamlUserType(this, fullName, type, GetXamlTypeByName("System.Enum"));
			xamlUserType25.StaticInitializer = StaticInitializer_6_NavigationViewPaneDisplayMode;
			xamlUserType25.AddEnumValue("Auto", NavigationViewPaneDisplayMode.Auto);
			xamlUserType25.AddEnumValue("Left", NavigationViewPaneDisplayMode.Left);
			xamlUserType25.AddEnumValue("Top", NavigationViewPaneDisplayMode.Top);
			xamlUserType25.AddEnumValue("LeftCompact", NavigationViewPaneDisplayMode.LeftCompact);
			xamlUserType25.AddEnumValue("LeftMinimal", NavigationViewPaneDisplayMode.LeftMinimal);
			result = xamlUserType25;
			break;
		}
		case 7:
			result = new XamlUserType(this, fullName, type, GetXamlTypeByName("System.ValueType"))
			{
				StaticInitializer = StaticInitializer_7_Enum
			};
			break;
		case 8:
			result = new XamlUserType(this, fullName, type, GetXamlTypeByName("Object"))
			{
				StaticInitializer = StaticInitializer_8_ValueType
			};
			break;
		case 9:
		{
			XamlUserType xamlUserType24 = new XamlUserType(this, fullName, type, GetXamlTypeByName("System.Enum"));
			xamlUserType24.StaticInitializer = StaticInitializer_9_NavigationViewBackButtonVisible;
			xamlUserType24.AddEnumValue("Collapsed", NavigationViewBackButtonVisible.Collapsed);
			xamlUserType24.AddEnumValue("Visible", NavigationViewBackButtonVisible.Visible);
			xamlUserType24.AddEnumValue("Auto", NavigationViewBackButtonVisible.Auto);
			result = xamlUserType24;
			break;
		}
		case 10:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 11:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 12:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 13:
		{
			XamlUserType xamlUserType23 = new XamlUserType(this, fullName, type, GetXamlTypeByName("System.Enum"));
			xamlUserType23.StaticInitializer = StaticInitializer_13_NavigationViewDisplayMode;
			xamlUserType23.AddEnumValue("Minimal", NavigationViewDisplayMode.Minimal);
			xamlUserType23.AddEnumValue("Compact", NavigationViewDisplayMode.Compact);
			xamlUserType23.AddEnumValue("Expanded", NavigationViewDisplayMode.Expanded);
			result = xamlUserType23;
			break;
		}
		case 14:
		{
			XamlUserType xamlUserType22 = new XamlUserType(this, fullName, type, null);
			xamlUserType22.StaticInitializer = StaticInitializer_14_IList;
			xamlUserType22.CollectionAdd = VectorAdd_14_IList;
			xamlUserType22.SetIsReturnTypeStub();
			result = xamlUserType22;
			break;
		}
		case 15:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 16:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 17:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 18:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 19:
		{
			XamlUserType xamlUserType21 = new XamlUserType(this, fullName, type, GetXamlTypeByName("System.Enum"));
			xamlUserType21.StaticInitializer = StaticInitializer_19_NavigationViewOverflowLabelMode;
			xamlUserType21.AddEnumValue("MoreLabel", NavigationViewOverflowLabelMode.MoreLabel);
			xamlUserType21.AddEnumValue("NoLabel", NavigationViewOverflowLabelMode.NoLabel);
			result = xamlUserType21;
			break;
		}
		case 20:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 21:
		{
			XamlUserType xamlUserType20 = new XamlUserType(this, fullName, type, GetXamlTypeByName("System.Enum"));
			xamlUserType20.StaticInitializer = StaticInitializer_21_NavigationViewSelectionFollowsFocus;
			xamlUserType20.AddEnumValue("Disabled", NavigationViewSelectionFollowsFocus.Disabled);
			xamlUserType20.AddEnumValue("Enabled", NavigationViewSelectionFollowsFocus.Enabled);
			result = xamlUserType20;
			break;
		}
		case 22:
		{
			XamlUserType xamlUserType19 = new XamlUserType(this, fullName, type, GetXamlTypeByName("System.Enum"));
			xamlUserType19.StaticInitializer = StaticInitializer_22_NavigationViewShoulderNavigationEnabled;
			xamlUserType19.AddEnumValue("WhenSelectionFollowsFocus", NavigationViewShoulderNavigationEnabled.WhenSelectionFollowsFocus);
			xamlUserType19.AddEnumValue("Always", NavigationViewShoulderNavigationEnabled.Always);
			xamlUserType19.AddEnumValue("Never", NavigationViewShoulderNavigationEnabled.Never);
			result = xamlUserType19;
			break;
		}
		case 23:
		{
			XamlUserType xamlUserType18 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.DependencyObject"));
			xamlUserType18.StaticInitializer = StaticInitializer_23_NavigationViewTemplateSettings;
			xamlUserType18.SetIsReturnTypeStub();
			result = xamlUserType18;
			break;
		}
		case 24:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 25:
		{
			XamlUserType xamlUserType17 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.Window"));
			xamlUserType17.Activator = Activate_25_MainWindow;
			xamlUserType17.StaticInitializer = StaticInitializer_25_MainWindow;
			xamlUserType17.AddMemberName("AppElementTheme");
			xamlUserType17.SetIsLocalType();
			result = xamlUserType17;
			break;
		}
		case 26:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 27:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 28:
		{
			XamlUserType xamlUserType16 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.Controls.Page"));
			xamlUserType16.Activator = Activate_28_AboutPage;
			xamlUserType16.StaticInitializer = StaticInitializer_28_AboutPage;
			xamlUserType16.SetIsLocalType();
			result = xamlUserType16;
			break;
		}
		case 29:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 30:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 31:
		{
			XamlUserType xamlUserType15 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.Controls.Page"));
			xamlUserType15.Activator = Activate_31_HomePage;
			xamlUserType15.StaticInitializer = StaticInitializer_31_HomePage;
			xamlUserType15.SetIsLocalType();
			result = xamlUserType15;
			break;
		}
		case 32:
		{
			XamlUserType xamlUserType14 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.Controls.Control"));
			xamlUserType14.Activator = Activate_32_InfoBar;
			xamlUserType14.StaticInitializer = StaticInitializer_32_InfoBar;
			xamlUserType14.SetContentPropertyName("Microsoft.UI.Xaml.Controls.InfoBar.Content");
			xamlUserType14.AddMemberName("Content");
			xamlUserType14.AddMemberName("IsOpen");
			xamlUserType14.AddMemberName("IsClosable");
			xamlUserType14.AddMemberName("ActionButton");
			xamlUserType14.AddMemberName("CloseButtonCommand");
			xamlUserType14.AddMemberName("CloseButtonCommandParameter");
			xamlUserType14.AddMemberName("CloseButtonStyle");
			xamlUserType14.AddMemberName("ContentTemplate");
			xamlUserType14.AddMemberName("IconSource");
			xamlUserType14.AddMemberName("IsIconVisible");
			xamlUserType14.AddMemberName("Message");
			xamlUserType14.AddMemberName("Severity");
			xamlUserType14.AddMemberName("TemplateSettings");
			xamlUserType14.AddMemberName("Title");
			result = xamlUserType14;
			break;
		}
		case 33:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 34:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 35:
		{
			XamlUserType xamlUserType13 = new XamlUserType(this, fullName, type, null);
			xamlUserType13.StaticInitializer = StaticInitializer_35_ICommand;
			xamlUserType13.SetIsReturnTypeStub();
			result = xamlUserType13;
			break;
		}
		case 36:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 37:
		{
			XamlUserType xamlUserType12 = new XamlUserType(this, fullName, type, GetXamlTypeByName("System.Enum"));
			xamlUserType12.StaticInitializer = StaticInitializer_37_InfoBarSeverity;
			xamlUserType12.AddEnumValue("Informational", InfoBarSeverity.Informational);
			xamlUserType12.AddEnumValue("Success", InfoBarSeverity.Success);
			xamlUserType12.AddEnumValue("Warning", InfoBarSeverity.Warning);
			xamlUserType12.AddEnumValue("Error", InfoBarSeverity.Error);
			result = xamlUserType12;
			break;
		}
		case 38:
		{
			XamlUserType xamlUserType11 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.DependencyObject"));
			xamlUserType11.StaticInitializer = StaticInitializer_38_InfoBarTemplateSettings;
			xamlUserType11.SetIsReturnTypeStub();
			result = xamlUserType11;
			break;
		}
		case 39:
		{
			XamlUserType xamlUserType10 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.Controls.Control"));
			xamlUserType10.Activator = Activate_39_ProgressRing;
			xamlUserType10.StaticInitializer = StaticInitializer_39_ProgressRing;
			xamlUserType10.AddMemberName("IsActive");
			xamlUserType10.AddMemberName("IsIndeterminate");
			xamlUserType10.AddMemberName("Maximum");
			xamlUserType10.AddMemberName("Minimum");
			xamlUserType10.AddMemberName("TemplateSettings");
			xamlUserType10.AddMemberName("Value");
			result = xamlUserType10;
			break;
		}
		case 40:
		{
			XamlUserType xamlUserType9 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.DependencyObject"));
			xamlUserType9.StaticInitializer = StaticInitializer_40_ProgressRingTemplateSettings;
			xamlUserType9.SetIsReturnTypeStub();
			result = xamlUserType9;
			break;
		}
		case 41:
		{
			XamlUserType xamlUserType8 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.Controls.Page"));
			xamlUserType8.Activator = Activate_41_LoginPage;
			xamlUserType8.StaticInitializer = StaticInitializer_41_LoginPage;
			xamlUserType8.SetIsLocalType();
			result = xamlUserType8;
			break;
		}
		case 42:
		{
			XamlUserType xamlUserType7 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.Controls.DataTemplateSelector"));
			xamlUserType7.Activator = Activate_42_StatusRowSelector;
			xamlUserType7.StaticInitializer = StaticInitializer_42_StatusRowSelector;
			xamlUserType7.AddMemberName("HeaderTemplate");
			xamlUserType7.AddMemberName("RoleTemplate");
			xamlUserType7.SetIsLocalType();
			result = xamlUserType7;
			break;
		}
		case 43:
		{
			XamlUserType xamlUserType6 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.Controls.Page"));
			xamlUserType6.Activator = Activate_43_ProxyCapturePage;
			xamlUserType6.StaticInitializer = StaticInitializer_43_ProxyCapturePage;
			xamlUserType6.SetIsLocalType();
			result = xamlUserType6;
			break;
		}
		case 44:
			result = new XamlSystemBaseType(fullName, type);
			break;
		case 45:
		{
			XamlUserType xamlUserType5 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.Controls.Page"));
			xamlUserType5.Activator = Activate_45_SettingsPage;
			xamlUserType5.StaticInitializer = StaticInitializer_45_SettingsPage;
			xamlUserType5.SetIsLocalType();
			result = xamlUserType5;
			break;
		}
		case 46:
		{
			result = null;
			break;
		}
		case 47:
		{
			XamlUserType xamlUserType3 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.Controls.Page"));
			xamlUserType3.Activator = Activate_47_ToolsPage;
			xamlUserType3.StaticInitializer = StaticInitializer_47_ToolsPage;
			xamlUserType3.SetIsLocalType();
			result = xamlUserType3;
			break;
		}
		case 48:
		{
			XamlUserType xamlUserType2 = new XamlUserType(this, fullName, type, GetXamlTypeByName("Microsoft.UI.Xaml.DependencyObject"));
			xamlUserType2.Activator = Activate_48_TreeViewNode;
			xamlUserType2.StaticInitializer = StaticInitializer_48_TreeViewNode;
			xamlUserType2.AddMemberName("Children");
			xamlUserType2.AddMemberName("Content");
			xamlUserType2.AddMemberName("Depth");
			xamlUserType2.AddMemberName("HasChildren");
			xamlUserType2.AddMemberName("HasUnrealizedChildren");
			xamlUserType2.AddMemberName("IsExpanded");
			xamlUserType2.AddMemberName("Parent");
			xamlUserType2.SetIsBindable();
			result = xamlUserType2;
			break;
		}
		case 49:
		{
			XamlUserType xamlUserType = new XamlUserType(this, fullName, type, null);
			xamlUserType.StaticInitializer = StaticInitializer_49_IList;
			xamlUserType.CollectionAdd = VectorAdd_49_IList;
			xamlUserType.SetIsReturnTypeStub();
			result = xamlUserType;
			break;
		}
		case 50:
			result = new XamlSystemBaseType(fullName, type);
			break;
		}
		return result;
	}

	private IXamlType CheckOtherMetadataProvidersForName(string typeName)
	{
		IXamlType xamlType = null;
		IXamlType result = null;
		foreach (IXamlMetadataProvider otherProvider in OtherProviders)
		{
			xamlType = otherProvider.GetXamlType(typeName);
			if (xamlType != null)
			{
				if (xamlType.IsConstructible)
				{
					return xamlType;
				}
				result = xamlType;
			}
		}
		return result;
	}

	private IXamlType CheckOtherMetadataProvidersForType(Type type)
	{
		IXamlType xamlType = null;
		IXamlType result = null;
		foreach (IXamlMetadataProvider otherProvider in OtherProviders)
		{
			xamlType = otherProvider.GetXamlType(type);
			if (xamlType != null)
			{
				if (xamlType.IsConstructible)
				{
					return xamlType;
				}
				result = xamlType;
			}
		}
		return result;
	}

	private object get_0_XamlControlsResources_UseCompactResources(object instance)
	{
		return ((XamlControlsResources)instance).UseCompactResources;
	}

	private void set_0_XamlControlsResources_UseCompactResources(object instance, object Value)
	{
		((XamlControlsResources)instance).UseCompactResources = (bool)Value;
	}

	private object get_1_NavigationView_PaneDisplayMode(object instance)
	{
		return ((NavigationView)instance).PaneDisplayMode;
	}

	private void set_1_NavigationView_PaneDisplayMode(object instance, object Value)
	{
		((NavigationView)instance).PaneDisplayMode = (NavigationViewPaneDisplayMode)Value;
	}

	private object get_2_NavigationView_IsPaneToggleButtonVisible(object instance)
	{
		return ((NavigationView)instance).IsPaneToggleButtonVisible;
	}

	private void set_2_NavigationView_IsPaneToggleButtonVisible(object instance, object Value)
	{
		((NavigationView)instance).IsPaneToggleButtonVisible = (bool)Value;
	}

	private object get_3_NavigationView_IsBackButtonVisible(object instance)
	{
		return ((NavigationView)instance).IsBackButtonVisible;
	}

	private void set_3_NavigationView_IsBackButtonVisible(object instance, object Value)
	{
		((NavigationView)instance).IsBackButtonVisible = (NavigationViewBackButtonVisible)Value;
	}

	private object get_4_NavigationView_IsSettingsVisible(object instance)
	{
		return ((NavigationView)instance).IsSettingsVisible;
	}

	private void set_4_NavigationView_IsSettingsVisible(object instance, object Value)
	{
		((NavigationView)instance).IsSettingsVisible = (bool)Value;
	}

	private object get_5_NavigationView_AlwaysShowHeader(object instance)
	{
		return ((NavigationView)instance).AlwaysShowHeader;
	}

	private void set_5_NavigationView_AlwaysShowHeader(object instance, object Value)
	{
		((NavigationView)instance).AlwaysShowHeader = (bool)Value;
	}

	private object get_6_NavigationView_OpenPaneLength(object instance)
	{
		return ((NavigationView)instance).OpenPaneLength;
	}

	private void set_6_NavigationView_OpenPaneLength(object instance, object Value)
	{
		((NavigationView)instance).OpenPaneLength = (double)Value;
	}

	private object get_7_NavigationView_AutoSuggestBox(object instance)
	{
		return ((NavigationView)instance).AutoSuggestBox;
	}

	private void set_7_NavigationView_AutoSuggestBox(object instance, object Value)
	{
		((NavigationView)instance).AutoSuggestBox = (AutoSuggestBox)Value;
	}

	private object get_8_NavigationView_CompactModeThresholdWidth(object instance)
	{
		return ((NavigationView)instance).CompactModeThresholdWidth;
	}

	private void set_8_NavigationView_CompactModeThresholdWidth(object instance, object Value)
	{
		((NavigationView)instance).CompactModeThresholdWidth = (double)Value;
	}

	private object get_9_NavigationView_CompactPaneLength(object instance)
	{
		return ((NavigationView)instance).CompactPaneLength;
	}

	private void set_9_NavigationView_CompactPaneLength(object instance, object Value)
	{
		((NavigationView)instance).CompactPaneLength = (double)Value;
	}

	private object get_10_NavigationView_ContentOverlay(object instance)
	{
		return ((NavigationView)instance).ContentOverlay;
	}

	private void set_10_NavigationView_ContentOverlay(object instance, object Value)
	{
		((NavigationView)instance).ContentOverlay = (UIElement)Value;
	}

	private object get_11_NavigationView_DisplayMode(object instance)
	{
		return ((NavigationView)instance).DisplayMode;
	}

	private object get_12_NavigationView_ExpandedModeThresholdWidth(object instance)
	{
		return ((NavigationView)instance).ExpandedModeThresholdWidth;
	}

	private void set_12_NavigationView_ExpandedModeThresholdWidth(object instance, object Value)
	{
		((NavigationView)instance).ExpandedModeThresholdWidth = (double)Value;
	}

	private object get_13_NavigationView_FooterMenuItems(object instance)
	{
		return ((NavigationView)instance).FooterMenuItems;
	}

	private object get_14_NavigationView_FooterMenuItemsSource(object instance)
	{
		return ((NavigationView)instance).FooterMenuItemsSource;
	}

	private void set_14_NavigationView_FooterMenuItemsSource(object instance, object Value)
	{
		((NavigationView)instance).FooterMenuItemsSource = Value;
	}

	private object get_15_NavigationView_Header(object instance)
	{
		return ((NavigationView)instance).Header;
	}

	private void set_15_NavigationView_Header(object instance, object Value)
	{
		((NavigationView)instance).Header = Value;
	}

	private object get_16_NavigationView_HeaderTemplate(object instance)
	{
		return ((NavigationView)instance).HeaderTemplate;
	}

	private void set_16_NavigationView_HeaderTemplate(object instance, object Value)
	{
		((NavigationView)instance).HeaderTemplate = (DataTemplate)Value;
	}

	private object get_17_NavigationView_IsBackEnabled(object instance)
	{
		return ((NavigationView)instance).IsBackEnabled;
	}

	private void set_17_NavigationView_IsBackEnabled(object instance, object Value)
	{
		((NavigationView)instance).IsBackEnabled = (bool)Value;
	}

	private object get_18_NavigationView_IsPaneOpen(object instance)
	{
		return ((NavigationView)instance).IsPaneOpen;
	}

	private void set_18_NavigationView_IsPaneOpen(object instance, object Value)
	{
		((NavigationView)instance).IsPaneOpen = (bool)Value;
	}

	private object get_19_NavigationView_IsPaneVisible(object instance)
	{
		return ((NavigationView)instance).IsPaneVisible;
	}

	private void set_19_NavigationView_IsPaneVisible(object instance, object Value)
	{
		((NavigationView)instance).IsPaneVisible = (bool)Value;
	}

	private object get_20_NavigationView_IsTitleBarAutoPaddingEnabled(object instance)
	{
		return ((NavigationView)instance).IsTitleBarAutoPaddingEnabled;
	}

	private void set_20_NavigationView_IsTitleBarAutoPaddingEnabled(object instance, object Value)
	{
		((NavigationView)instance).IsTitleBarAutoPaddingEnabled = (bool)Value;
	}

	private object get_21_NavigationView_MenuItemContainerStyle(object instance)
	{
		return ((NavigationView)instance).MenuItemContainerStyle;
	}

	private void set_21_NavigationView_MenuItemContainerStyle(object instance, object Value)
	{
		((NavigationView)instance).MenuItemContainerStyle = (Style)Value;
	}

	private object get_22_NavigationView_MenuItemContainerStyleSelector(object instance)
	{
		return ((NavigationView)instance).MenuItemContainerStyleSelector;
	}

	private void set_22_NavigationView_MenuItemContainerStyleSelector(object instance, object Value)
	{
		((NavigationView)instance).MenuItemContainerStyleSelector = (StyleSelector)Value;
	}

	private object get_23_NavigationView_MenuItemTemplate(object instance)
	{
		return ((NavigationView)instance).MenuItemTemplate;
	}

	private void set_23_NavigationView_MenuItemTemplate(object instance, object Value)
	{
		((NavigationView)instance).MenuItemTemplate = (DataTemplate)Value;
	}

	private object get_24_NavigationView_MenuItemTemplateSelector(object instance)
	{
		return ((NavigationView)instance).MenuItemTemplateSelector;
	}

	private void set_24_NavigationView_MenuItemTemplateSelector(object instance, object Value)
	{
		((NavigationView)instance).MenuItemTemplateSelector = (DataTemplateSelector)Value;
	}

	private object get_25_NavigationView_MenuItems(object instance)
	{
		return ((NavigationView)instance).MenuItems;
	}

	private object get_26_NavigationView_MenuItemsSource(object instance)
	{
		return ((NavigationView)instance).MenuItemsSource;
	}

	private void set_26_NavigationView_MenuItemsSource(object instance, object Value)
	{
		((NavigationView)instance).MenuItemsSource = Value;
	}

	private object get_27_NavigationView_OverflowLabelMode(object instance)
	{
		return ((NavigationView)instance).OverflowLabelMode;
	}

	private void set_27_NavigationView_OverflowLabelMode(object instance, object Value)
	{
		((NavigationView)instance).OverflowLabelMode = (NavigationViewOverflowLabelMode)Value;
	}

	private object get_28_NavigationView_PaneCustomContent(object instance)
	{
		return ((NavigationView)instance).PaneCustomContent;
	}

	private void set_28_NavigationView_PaneCustomContent(object instance, object Value)
	{
		((NavigationView)instance).PaneCustomContent = (UIElement)Value;
	}

	private object get_29_NavigationView_PaneFooter(object instance)
	{
		return ((NavigationView)instance).PaneFooter;
	}

	private void set_29_NavigationView_PaneFooter(object instance, object Value)
	{
		((NavigationView)instance).PaneFooter = (UIElement)Value;
	}

	private object get_30_NavigationView_PaneHeader(object instance)
	{
		return ((NavigationView)instance).PaneHeader;
	}

	private void set_30_NavigationView_PaneHeader(object instance, object Value)
	{
		((NavigationView)instance).PaneHeader = (UIElement)Value;
	}

	private object get_31_NavigationView_PaneTitle(object instance)
	{
		return ((NavigationView)instance).PaneTitle;
	}

	private void set_31_NavigationView_PaneTitle(object instance, object Value)
	{
		((NavigationView)instance).PaneTitle = (string)Value;
	}

	private object get_32_NavigationView_PaneToggleButtonStyle(object instance)
	{
		return ((NavigationView)instance).PaneToggleButtonStyle;
	}

	private void set_32_NavigationView_PaneToggleButtonStyle(object instance, object Value)
	{
		((NavigationView)instance).PaneToggleButtonStyle = (Style)Value;
	}

	private object get_33_NavigationView_SelectedItem(object instance)
	{
		return ((NavigationView)instance).SelectedItem;
	}

	private void set_33_NavigationView_SelectedItem(object instance, object Value)
	{
		((NavigationView)instance).SelectedItem = Value;
	}

	private object get_34_NavigationView_SelectionFollowsFocus(object instance)
	{
		return ((NavigationView)instance).SelectionFollowsFocus;
	}

	private void set_34_NavigationView_SelectionFollowsFocus(object instance, object Value)
	{
		((NavigationView)instance).SelectionFollowsFocus = (NavigationViewSelectionFollowsFocus)Value;
	}

	private object get_35_NavigationView_SettingsItem(object instance)
	{
		return ((NavigationView)instance).SettingsItem;
	}

	private object get_36_NavigationView_ShoulderNavigationEnabled(object instance)
	{
		return ((NavigationView)instance).ShoulderNavigationEnabled;
	}

	private void set_36_NavigationView_ShoulderNavigationEnabled(object instance, object Value)
	{
		((NavigationView)instance).ShoulderNavigationEnabled = (NavigationViewShoulderNavigationEnabled)Value;
	}

	private object get_37_NavigationView_TemplateSettings(object instance)
	{
		return ((NavigationView)instance).TemplateSettings;
	}

	private object get_38_MainWindow_AppElementTheme(object instance)
	{
		return ((MainWindow)instance).AppElementTheme;
	}

	private object get_39_InfoBar_Content(object instance)
	{
		return ((InfoBar)instance).Content;
	}

	private void set_39_InfoBar_Content(object instance, object Value)
	{
		((InfoBar)instance).Content = Value;
	}

	private object get_40_InfoBar_IsOpen(object instance)
	{
		return ((InfoBar)instance).IsOpen;
	}

	private void set_40_InfoBar_IsOpen(object instance, object Value)
	{
		((InfoBar)instance).IsOpen = (bool)Value;
	}

	private object get_41_InfoBar_IsClosable(object instance)
	{
		return ((InfoBar)instance).IsClosable;
	}

	private void set_41_InfoBar_IsClosable(object instance, object Value)
	{
		((InfoBar)instance).IsClosable = (bool)Value;
	}

	private object get_42_InfoBar_ActionButton(object instance)
	{
		return ((InfoBar)instance).ActionButton;
	}

	private void set_42_InfoBar_ActionButton(object instance, object Value)
	{
		((InfoBar)instance).ActionButton = (ButtonBase)Value;
	}

	private object get_43_InfoBar_CloseButtonCommand(object instance)
	{
		return ((InfoBar)instance).CloseButtonCommand;
	}

	private void set_43_InfoBar_CloseButtonCommand(object instance, object Value)
	{
		((InfoBar)instance).CloseButtonCommand = (ICommand)Value;
	}

	private object get_44_InfoBar_CloseButtonCommandParameter(object instance)
	{
		return ((InfoBar)instance).CloseButtonCommandParameter;
	}

	private void set_44_InfoBar_CloseButtonCommandParameter(object instance, object Value)
	{
		((InfoBar)instance).CloseButtonCommandParameter = Value;
	}

	private object get_45_InfoBar_CloseButtonStyle(object instance)
	{
		return ((InfoBar)instance).CloseButtonStyle;
	}

	private void set_45_InfoBar_CloseButtonStyle(object instance, object Value)
	{
		((InfoBar)instance).CloseButtonStyle = (Style)Value;
	}

	private object get_46_InfoBar_ContentTemplate(object instance)
	{
		return ((InfoBar)instance).ContentTemplate;
	}

	private void set_46_InfoBar_ContentTemplate(object instance, object Value)
	{
		((InfoBar)instance).ContentTemplate = (DataTemplate)Value;
	}

	private object get_47_InfoBar_IconSource(object instance)
	{
		return ((InfoBar)instance).IconSource;
	}

	private void set_47_InfoBar_IconSource(object instance, object Value)
	{
		((InfoBar)instance).IconSource = (IconSource)Value;
	}

	private object get_48_InfoBar_IsIconVisible(object instance)
	{
		return ((InfoBar)instance).IsIconVisible;
	}

	private void set_48_InfoBar_IsIconVisible(object instance, object Value)
	{
		((InfoBar)instance).IsIconVisible = (bool)Value;
	}

	private object get_49_InfoBar_Message(object instance)
	{
		return ((InfoBar)instance).Message;
	}

	private void set_49_InfoBar_Message(object instance, object Value)
	{
		((InfoBar)instance).Message = (string)Value;
	}

	private object get_50_InfoBar_Severity(object instance)
	{
		return ((InfoBar)instance).Severity;
	}

	private void set_50_InfoBar_Severity(object instance, object Value)
	{
		((InfoBar)instance).Severity = (InfoBarSeverity)Value;
	}

	private object get_51_InfoBar_TemplateSettings(object instance)
	{
		return ((InfoBar)instance).TemplateSettings;
	}

	private object get_52_InfoBar_Title(object instance)
	{
		return ((InfoBar)instance).Title;
	}

	private void set_52_InfoBar_Title(object instance, object Value)
	{
		((InfoBar)instance).Title = (string)Value;
	}

	private object get_53_ProgressRing_IsActive(object instance)
	{
		return ((ProgressRing)instance).IsActive;
	}

	private void set_53_ProgressRing_IsActive(object instance, object Value)
	{
		((ProgressRing)instance).IsActive = (bool)Value;
	}

	private object get_54_ProgressRing_IsIndeterminate(object instance)
	{
		return ((ProgressRing)instance).IsIndeterminate;
	}

	private void set_54_ProgressRing_IsIndeterminate(object instance, object Value)
	{
		((ProgressRing)instance).IsIndeterminate = (bool)Value;
	}

	private object get_55_ProgressRing_Maximum(object instance)
	{
		return ((ProgressRing)instance).Maximum;
	}

	private void set_55_ProgressRing_Maximum(object instance, object Value)
	{
		((ProgressRing)instance).Maximum = (double)Value;
	}

	private object get_56_ProgressRing_Minimum(object instance)
	{
		return ((ProgressRing)instance).Minimum;
	}

	private void set_56_ProgressRing_Minimum(object instance, object Value)
	{
		((ProgressRing)instance).Minimum = (double)Value;
	}

	private object get_57_ProgressRing_TemplateSettings(object instance)
	{
		return ((ProgressRing)instance).TemplateSettings;
	}

	private object get_58_ProgressRing_Value(object instance)
	{
		return ((ProgressRing)instance).Value;
	}

	private void set_58_ProgressRing_Value(object instance, object Value)
	{
		((ProgressRing)instance).Value = (double)Value;
	}

	private object get_59_StatusRowSelector_HeaderTemplate(object instance)
	{
		return ((StatusRowSelector)instance).HeaderTemplate;
	}

	private void set_59_StatusRowSelector_HeaderTemplate(object instance, object Value)
	{
		((StatusRowSelector)instance).HeaderTemplate = (DataTemplate)Value;
	}

	private object get_60_StatusRowSelector_RoleTemplate(object instance)
	{
		return ((StatusRowSelector)instance).RoleTemplate;
	}

	private void set_60_StatusRowSelector_RoleTemplate(object instance, object Value)
	{
		((StatusRowSelector)instance).RoleTemplate = (DataTemplate)Value;
	}

	private object get_61_TreeViewNode_Children(object instance)
	{
		return ((TreeViewNode)instance).Children;
	}

	private object get_62_TreeViewNode_Content(object instance)
	{
		return ((TreeViewNode)instance).Content;
	}

	private void set_62_TreeViewNode_Content(object instance, object Value)
	{
		((TreeViewNode)instance).Content = Value;
	}

	private object get_63_TreeViewNode_Depth(object instance)
	{
		return ((TreeViewNode)instance).Depth;
	}

	private object get_64_TreeViewNode_HasChildren(object instance)
	{
		return ((TreeViewNode)instance).HasChildren;
	}

	private object get_65_TreeViewNode_HasUnrealizedChildren(object instance)
	{
		return ((TreeViewNode)instance).HasUnrealizedChildren;
	}

	private void set_65_TreeViewNode_HasUnrealizedChildren(object instance, object Value)
	{
		((TreeViewNode)instance).HasUnrealizedChildren = (bool)Value;
	}

	private object get_66_TreeViewNode_IsExpanded(object instance)
	{
		return ((TreeViewNode)instance).IsExpanded;
	}

	private void set_66_TreeViewNode_IsExpanded(object instance, object Value)
	{
		((TreeViewNode)instance).IsExpanded = (bool)Value;
	}

	private object get_67_TreeViewNode_Parent(object instance)
	{
		return ((TreeViewNode)instance).Parent;
	}

	private IXamlMember CreateXamlMember(string longMemberName)
	{
		XamlMember xamlMember = null;
		switch (longMemberName)
		{
		case "Microsoft.UI.Xaml.Controls.XamlControlsResources.UseCompactResources":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.XamlControlsResources");
			xamlMember = new XamlMember(this, "UseCompactResources", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_0_XamlControlsResources_UseCompactResources;
			xamlMember.Setter = set_0_XamlControlsResources_UseCompactResources;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.PaneDisplayMode":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "PaneDisplayMode", "Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_1_NavigationView_PaneDisplayMode;
			xamlMember.Setter = set_1_NavigationView_PaneDisplayMode;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.IsPaneToggleButtonVisible":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "IsPaneToggleButtonVisible", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_2_NavigationView_IsPaneToggleButtonVisible;
			xamlMember.Setter = set_2_NavigationView_IsPaneToggleButtonVisible;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.IsBackButtonVisible":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "IsBackButtonVisible", "Microsoft.UI.Xaml.Controls.NavigationViewBackButtonVisible");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_3_NavigationView_IsBackButtonVisible;
			xamlMember.Setter = set_3_NavigationView_IsBackButtonVisible;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.IsSettingsVisible":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "IsSettingsVisible", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_4_NavigationView_IsSettingsVisible;
			xamlMember.Setter = set_4_NavigationView_IsSettingsVisible;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.AlwaysShowHeader":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "AlwaysShowHeader", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_5_NavigationView_AlwaysShowHeader;
			xamlMember.Setter = set_5_NavigationView_AlwaysShowHeader;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.OpenPaneLength":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "OpenPaneLength", "Double");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_6_NavigationView_OpenPaneLength;
			xamlMember.Setter = set_6_NavigationView_OpenPaneLength;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.AutoSuggestBox":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "AutoSuggestBox", "Microsoft.UI.Xaml.Controls.AutoSuggestBox");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_7_NavigationView_AutoSuggestBox;
			xamlMember.Setter = set_7_NavigationView_AutoSuggestBox;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.CompactModeThresholdWidth":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "CompactModeThresholdWidth", "Double");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_8_NavigationView_CompactModeThresholdWidth;
			xamlMember.Setter = set_8_NavigationView_CompactModeThresholdWidth;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.CompactPaneLength":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "CompactPaneLength", "Double");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_9_NavigationView_CompactPaneLength;
			xamlMember.Setter = set_9_NavigationView_CompactPaneLength;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.ContentOverlay":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "ContentOverlay", "Microsoft.UI.Xaml.UIElement");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_10_NavigationView_ContentOverlay;
			xamlMember.Setter = set_10_NavigationView_ContentOverlay;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.DisplayMode":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "DisplayMode", "Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_11_NavigationView_DisplayMode;
			xamlMember.SetIsReadOnly();
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.ExpandedModeThresholdWidth":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "ExpandedModeThresholdWidth", "Double");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_12_NavigationView_ExpandedModeThresholdWidth;
			xamlMember.Setter = set_12_NavigationView_ExpandedModeThresholdWidth;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.FooterMenuItems":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "FooterMenuItems", "System.Collections.Generic.IList`1<Object>");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_13_NavigationView_FooterMenuItems;
			xamlMember.SetIsReadOnly();
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.FooterMenuItemsSource":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "FooterMenuItemsSource", "Object");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_14_NavigationView_FooterMenuItemsSource;
			xamlMember.Setter = set_14_NavigationView_FooterMenuItemsSource;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.Header":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "Header", "Object");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_15_NavigationView_Header;
			xamlMember.Setter = set_15_NavigationView_Header;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.HeaderTemplate":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "HeaderTemplate", "Microsoft.UI.Xaml.DataTemplate");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_16_NavigationView_HeaderTemplate;
			xamlMember.Setter = set_16_NavigationView_HeaderTemplate;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.IsBackEnabled":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "IsBackEnabled", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_17_NavigationView_IsBackEnabled;
			xamlMember.Setter = set_17_NavigationView_IsBackEnabled;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.IsPaneOpen":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "IsPaneOpen", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_18_NavigationView_IsPaneOpen;
			xamlMember.Setter = set_18_NavigationView_IsPaneOpen;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.IsPaneVisible":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "IsPaneVisible", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_19_NavigationView_IsPaneVisible;
			xamlMember.Setter = set_19_NavigationView_IsPaneVisible;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.IsTitleBarAutoPaddingEnabled":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "IsTitleBarAutoPaddingEnabled", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_20_NavigationView_IsTitleBarAutoPaddingEnabled;
			xamlMember.Setter = set_20_NavigationView_IsTitleBarAutoPaddingEnabled;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.MenuItemContainerStyle":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "MenuItemContainerStyle", "Microsoft.UI.Xaml.Style");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_21_NavigationView_MenuItemContainerStyle;
			xamlMember.Setter = set_21_NavigationView_MenuItemContainerStyle;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.MenuItemContainerStyleSelector":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "MenuItemContainerStyleSelector", "Microsoft.UI.Xaml.Controls.StyleSelector");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_22_NavigationView_MenuItemContainerStyleSelector;
			xamlMember.Setter = set_22_NavigationView_MenuItemContainerStyleSelector;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.MenuItemTemplate":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "MenuItemTemplate", "Microsoft.UI.Xaml.DataTemplate");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_23_NavigationView_MenuItemTemplate;
			xamlMember.Setter = set_23_NavigationView_MenuItemTemplate;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.MenuItemTemplateSelector":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "MenuItemTemplateSelector", "Microsoft.UI.Xaml.Controls.DataTemplateSelector");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_24_NavigationView_MenuItemTemplateSelector;
			xamlMember.Setter = set_24_NavigationView_MenuItemTemplateSelector;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.MenuItems":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "MenuItems", "System.Collections.Generic.IList`1<Object>");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_25_NavigationView_MenuItems;
			xamlMember.SetIsReadOnly();
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.MenuItemsSource":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "MenuItemsSource", "Object");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_26_NavigationView_MenuItemsSource;
			xamlMember.Setter = set_26_NavigationView_MenuItemsSource;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.OverflowLabelMode":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "OverflowLabelMode", "Microsoft.UI.Xaml.Controls.NavigationViewOverflowLabelMode");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_27_NavigationView_OverflowLabelMode;
			xamlMember.Setter = set_27_NavigationView_OverflowLabelMode;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.PaneCustomContent":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "PaneCustomContent", "Microsoft.UI.Xaml.UIElement");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_28_NavigationView_PaneCustomContent;
			xamlMember.Setter = set_28_NavigationView_PaneCustomContent;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.PaneFooter":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "PaneFooter", "Microsoft.UI.Xaml.UIElement");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_29_NavigationView_PaneFooter;
			xamlMember.Setter = set_29_NavigationView_PaneFooter;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.PaneHeader":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "PaneHeader", "Microsoft.UI.Xaml.UIElement");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_30_NavigationView_PaneHeader;
			xamlMember.Setter = set_30_NavigationView_PaneHeader;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.PaneTitle":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "PaneTitle", "String");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_31_NavigationView_PaneTitle;
			xamlMember.Setter = set_31_NavigationView_PaneTitle;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.PaneToggleButtonStyle":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "PaneToggleButtonStyle", "Microsoft.UI.Xaml.Style");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_32_NavigationView_PaneToggleButtonStyle;
			xamlMember.Setter = set_32_NavigationView_PaneToggleButtonStyle;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.SelectedItem":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "SelectedItem", "Object");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_33_NavigationView_SelectedItem;
			xamlMember.Setter = set_33_NavigationView_SelectedItem;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.SelectionFollowsFocus":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "SelectionFollowsFocus", "Microsoft.UI.Xaml.Controls.NavigationViewSelectionFollowsFocus");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_34_NavigationView_SelectionFollowsFocus;
			xamlMember.Setter = set_34_NavigationView_SelectionFollowsFocus;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.SettingsItem":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "SettingsItem", "Object");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_35_NavigationView_SettingsItem;
			xamlMember.SetIsReadOnly();
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.ShoulderNavigationEnabled":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "ShoulderNavigationEnabled", "Microsoft.UI.Xaml.Controls.NavigationViewShoulderNavigationEnabled");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_36_NavigationView_ShoulderNavigationEnabled;
			xamlMember.Setter = set_36_NavigationView_ShoulderNavigationEnabled;
			break;
		case "Microsoft.UI.Xaml.Controls.NavigationView.TemplateSettings":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.NavigationView");
			xamlMember = new XamlMember(this, "TemplateSettings", "Microsoft.UI.Xaml.Controls.NavigationViewTemplateSettings");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_37_NavigationView_TemplateSettings;
			xamlMember.SetIsReadOnly();
			break;
		case "MhyunBox.MainWindow.AppElementTheme":
			_ = (XamlUserType)GetXamlTypeByName("MhyunBox.MainWindow");
			xamlMember = new XamlMember(this, "AppElementTheme", "Microsoft.UI.Xaml.ElementTheme");
			xamlMember.Getter = get_38_MainWindow_AppElementTheme;
			xamlMember.SetIsReadOnly();
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.Content":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "Content", "Object");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_39_InfoBar_Content;
			xamlMember.Setter = set_39_InfoBar_Content;
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.IsOpen":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "IsOpen", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_40_InfoBar_IsOpen;
			xamlMember.Setter = set_40_InfoBar_IsOpen;
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.IsClosable":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "IsClosable", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_41_InfoBar_IsClosable;
			xamlMember.Setter = set_41_InfoBar_IsClosable;
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.ActionButton":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "ActionButton", "Microsoft.UI.Xaml.Controls.Primitives.ButtonBase");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_42_InfoBar_ActionButton;
			xamlMember.Setter = set_42_InfoBar_ActionButton;
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.CloseButtonCommand":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "CloseButtonCommand", "System.Windows.Input.ICommand");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_43_InfoBar_CloseButtonCommand;
			xamlMember.Setter = set_43_InfoBar_CloseButtonCommand;
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.CloseButtonCommandParameter":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "CloseButtonCommandParameter", "Object");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_44_InfoBar_CloseButtonCommandParameter;
			xamlMember.Setter = set_44_InfoBar_CloseButtonCommandParameter;
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.CloseButtonStyle":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "CloseButtonStyle", "Microsoft.UI.Xaml.Style");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_45_InfoBar_CloseButtonStyle;
			xamlMember.Setter = set_45_InfoBar_CloseButtonStyle;
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.ContentTemplate":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "ContentTemplate", "Microsoft.UI.Xaml.DataTemplate");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_46_InfoBar_ContentTemplate;
			xamlMember.Setter = set_46_InfoBar_ContentTemplate;
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.IconSource":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "IconSource", "Microsoft.UI.Xaml.Controls.IconSource");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_47_InfoBar_IconSource;
			xamlMember.Setter = set_47_InfoBar_IconSource;
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.IsIconVisible":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "IsIconVisible", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_48_InfoBar_IsIconVisible;
			xamlMember.Setter = set_48_InfoBar_IsIconVisible;
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.Message":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "Message", "String");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_49_InfoBar_Message;
			xamlMember.Setter = set_49_InfoBar_Message;
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.Severity":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "Severity", "Microsoft.UI.Xaml.Controls.InfoBarSeverity");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_50_InfoBar_Severity;
			xamlMember.Setter = set_50_InfoBar_Severity;
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.TemplateSettings":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "TemplateSettings", "Microsoft.UI.Xaml.Controls.InfoBarTemplateSettings");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_51_InfoBar_TemplateSettings;
			xamlMember.SetIsReadOnly();
			break;
		case "Microsoft.UI.Xaml.Controls.InfoBar.Title":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.InfoBar");
			xamlMember = new XamlMember(this, "Title", "String");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_52_InfoBar_Title;
			xamlMember.Setter = set_52_InfoBar_Title;
			break;
		case "Microsoft.UI.Xaml.Controls.ProgressRing.IsActive":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.ProgressRing");
			xamlMember = new XamlMember(this, "IsActive", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_53_ProgressRing_IsActive;
			xamlMember.Setter = set_53_ProgressRing_IsActive;
			break;
		case "Microsoft.UI.Xaml.Controls.ProgressRing.IsIndeterminate":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.ProgressRing");
			xamlMember = new XamlMember(this, "IsIndeterminate", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_54_ProgressRing_IsIndeterminate;
			xamlMember.Setter = set_54_ProgressRing_IsIndeterminate;
			break;
		case "Microsoft.UI.Xaml.Controls.ProgressRing.Maximum":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.ProgressRing");
			xamlMember = new XamlMember(this, "Maximum", "Double");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_55_ProgressRing_Maximum;
			xamlMember.Setter = set_55_ProgressRing_Maximum;
			break;
		case "Microsoft.UI.Xaml.Controls.ProgressRing.Minimum":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.ProgressRing");
			xamlMember = new XamlMember(this, "Minimum", "Double");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_56_ProgressRing_Minimum;
			xamlMember.Setter = set_56_ProgressRing_Minimum;
			break;
		case "Microsoft.UI.Xaml.Controls.ProgressRing.TemplateSettings":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.ProgressRing");
			xamlMember = new XamlMember(this, "TemplateSettings", "Microsoft.UI.Xaml.Controls.ProgressRingTemplateSettings");
			xamlMember.Getter = get_57_ProgressRing_TemplateSettings;
			xamlMember.SetIsReadOnly();
			break;
		case "Microsoft.UI.Xaml.Controls.ProgressRing.Value":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.ProgressRing");
			xamlMember = new XamlMember(this, "Value", "Double");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_58_ProgressRing_Value;
			xamlMember.Setter = set_58_ProgressRing_Value;
			break;
		case "MhyunBox.Pages.StatusRowSelector.HeaderTemplate":
			_ = (XamlUserType)GetXamlTypeByName("MhyunBox.Pages.StatusRowSelector");
			xamlMember = new XamlMember(this, "HeaderTemplate", "Microsoft.UI.Xaml.DataTemplate");
			xamlMember.Getter = get_59_StatusRowSelector_HeaderTemplate;
			xamlMember.Setter = set_59_StatusRowSelector_HeaderTemplate;
			break;
		case "MhyunBox.Pages.StatusRowSelector.RoleTemplate":
			_ = (XamlUserType)GetXamlTypeByName("MhyunBox.Pages.StatusRowSelector");
			xamlMember = new XamlMember(this, "RoleTemplate", "Microsoft.UI.Xaml.DataTemplate");
			xamlMember.Getter = get_60_StatusRowSelector_RoleTemplate;
			xamlMember.Setter = set_60_StatusRowSelector_RoleTemplate;
			break;
		case "Microsoft.UI.Xaml.Controls.TreeViewNode.Children":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.TreeViewNode");
			xamlMember = new XamlMember(this, "Children", "System.Collections.Generic.IList`1<Microsoft.UI.Xaml.Controls.TreeViewNode>");
			xamlMember.Getter = get_61_TreeViewNode_Children;
			xamlMember.SetIsReadOnly();
			break;
		case "Microsoft.UI.Xaml.Controls.TreeViewNode.Content":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.TreeViewNode");
			xamlMember = new XamlMember(this, "Content", "Object");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_62_TreeViewNode_Content;
			xamlMember.Setter = set_62_TreeViewNode_Content;
			break;
		case "Microsoft.UI.Xaml.Controls.TreeViewNode.Depth":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.TreeViewNode");
			xamlMember = new XamlMember(this, "Depth", "Int32");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_63_TreeViewNode_Depth;
			xamlMember.SetIsReadOnly();
			break;
		case "Microsoft.UI.Xaml.Controls.TreeViewNode.HasChildren":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.TreeViewNode");
			xamlMember = new XamlMember(this, "HasChildren", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_64_TreeViewNode_HasChildren;
			xamlMember.SetIsReadOnly();
			break;
		case "Microsoft.UI.Xaml.Controls.TreeViewNode.HasUnrealizedChildren":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.TreeViewNode");
			xamlMember = new XamlMember(this, "HasUnrealizedChildren", "Boolean");
			xamlMember.Getter = get_65_TreeViewNode_HasUnrealizedChildren;
			xamlMember.Setter = set_65_TreeViewNode_HasUnrealizedChildren;
			break;
		case "Microsoft.UI.Xaml.Controls.TreeViewNode.IsExpanded":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.TreeViewNode");
			xamlMember = new XamlMember(this, "IsExpanded", "Boolean");
			xamlMember.SetIsDependencyProperty();
			xamlMember.Getter = get_66_TreeViewNode_IsExpanded;
			xamlMember.Setter = set_66_TreeViewNode_IsExpanded;
			break;
		case "Microsoft.UI.Xaml.Controls.TreeViewNode.Parent":
			_ = (XamlUserType)GetXamlTypeByName("Microsoft.UI.Xaml.Controls.TreeViewNode");
			xamlMember = new XamlMember(this, "Parent", "Microsoft.UI.Xaml.Controls.TreeViewNode");
			xamlMember.Getter = get_67_TreeViewNode_Parent;
			xamlMember.SetIsReadOnly();
			break;
		}
		return xamlMember;
	}
}
