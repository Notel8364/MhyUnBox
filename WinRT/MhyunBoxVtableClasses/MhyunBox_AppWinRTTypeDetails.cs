using System.Runtime.InteropServices;
using ABI.Microsoft.UI.Xaml;
using ABI.Microsoft.UI.Xaml.Markup;

namespace WinRT.MhyunBoxVtableClasses;

internal sealed class MhyunBox_AppWinRTTypeDetails : IWinRTExposedTypeDetails
{
	public ComWrappers.ComInterfaceEntry[] GetExposedInterfaces()
	{
		return new ComWrappers.ComInterfaceEntry[2]
		{
			new ComWrappers.ComInterfaceEntry
			{
				IID = IApplicationOverridesMethods.IID,
				Vtable = IApplicationOverridesMethods.AbiToProjectionVftablePtr
			},
			new ComWrappers.ComInterfaceEntry
			{
				IID = IXamlMetadataProviderMethods.IID,
				Vtable = IXamlMetadataProviderMethods.AbiToProjectionVftablePtr
			}
		};
	}
}
