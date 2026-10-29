using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ABI.System;

namespace WinRT.MhyunBoxGenericHelpers;

internal static class GlobalVtableLookup
{
	[ModuleInitializer]
	internal static void InitializeGlobalVtableLookup()
	{
		ComWrappersSupport.RegisterTypeComInterfaceEntriesLookup(LookupVtableEntries);
		ComWrappersSupport.RegisterTypeRuntimeClassNameLookup(LookupRuntimeClassName);
	}

	private static ComWrappers.ComInterfaceEntry[] LookupVtableEntries(System.Type type)
	{
		switch (type.ToString())
		{
		case "System.Net.Sockets.NetworkStream":
		case "System.IO.MemoryStream":
		case "System.Net.Security.SslStream":
			return new ComWrappers.ComInterfaceEntry[1]
			{
				new ComWrappers.ComInterfaceEntry
				{
					IID = IDisposableMethods.IID,
					Vtable = IDisposableMethods.AbiToProjectionVftablePtr
				}
			};
		default:
			return null;
		}
	}

	private static string LookupRuntimeClassName(System.Type type)
	{
		switch (type.ToString())
		{
		case "System.Net.Sockets.NetworkStream":
		case "System.IO.MemoryStream":
		case "System.Net.Security.SslStream":
			return "Windows.Foundation.IClosable";
		default:
			return null;
		}
	}
}
