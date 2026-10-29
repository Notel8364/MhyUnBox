using System.Runtime.InteropServices;
using ABI.Microsoft.UI.Xaml.Markup;

namespace WinRT.MhyunBoxVtableClasses;

internal sealed class MhyunBox_MhyunBox_XamlTypeInfo_XamlUserTypeWinRTTypeDetails : IWinRTExposedTypeDetails
{
	public ComWrappers.ComInterfaceEntry[] GetExposedInterfaces()
	{
		return new ComWrappers.ComInterfaceEntry[1]
		{
			new ComWrappers.ComInterfaceEntry
			{
				IID = IXamlTypeMethods.IID,
				Vtable = IXamlTypeMethods.AbiToProjectionVftablePtr
			}
		};
	}
}
