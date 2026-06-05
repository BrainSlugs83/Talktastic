using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Talktastic;

/// <summary>
/// Opens the Windows "Add a voice" dialog via UI Automation COM (raw vtable, AOT-safe).
/// No [ComImport], no reflection, no managed UIAutomation assemblies -- just raw COM pointers.
/// </summary>
static partial class VoiceInstaller
{
	// COM CLSIDs / IIDs
	static readonly Guid CLSID_CUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
	static readonly Guid IID_IUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
	static readonly Guid IID_IUIAutomationInvokePattern = new("FB377FBE-8EA6-46D5-9C73-6499642D3059");

	// UIA property / pattern IDs
	const int UIA_NamePropertyId = 30005;
	const int UIA_AutomationIdPropertyId = 30011;
	const int UIA_InvokePatternId = 10000;

	// TreeScope
	const int TreeScope_Children = 2;
	const int TreeScope_Descendants = 4;

	// VARIANT type tag
	const ushort VT_BSTR = 8;

	const int SW_RESTORE = 9;
	const string AddButtonAutomationId =
		"SystemSettings_Accessibility_Narrator_AddHighQualityVoice2_Button";

	// VARIANT: 24 bytes on x64. Passed by value in COM IDL,
	// but the x64 ABI converts >8-byte structs to hidden pointer.
	[StructLayout(LayoutKind.Explicit, Size = 24)]
	struct Variant
	{
		[FieldOffset(0)] public ushort vt;
		[FieldOffset(8)] public nint bstrVal;
	}

	// ── Vtable slot indices (verified against Windows SDK 10.0.26100.0 header) ──

	// IUIAutomation (after IUnknown 0-2)
	const int Slot_Auto_GetRootElement = 5;
	const int Slot_Auto_CreatePropertyCondition = 23;

	// IUIAutomationElement (after IUnknown 0-2)
	const int Slot_Elem_FindFirst = 5;
	const int Slot_Elem_GetCurrentPatternAs = 14;
	const int Slot_Elem_NativeWindowHandle = 36;

	// IUIAutomationInvokePattern (after IUnknown 0-2)
	const int Slot_Invoke_Invoke = 3;

	// ── P/Invoke ──

	[LibraryImport("ole32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int CoCreateInstance(
		in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

	[LibraryImport("ole32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int CoInitializeEx(nint reserved, uint dwCoInit);

	[LibraryImport("oleaut32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial nint SysAllocString(
		[MarshalAs(UnmanagedType.LPWStr)] string str);

	[LibraryImport("oleaut32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial void SysFreeString(nint bstr);

	[LibraryImport("user32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetForegroundWindow(nint hwnd);

	[LibraryImport("user32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ShowWindow(nint hwnd, int nCmdShow);

	// ── Helpers ──

	static unsafe nint* Vtable(nint pObj) => *(nint**)pObj;

	static unsafe void Release(nint pObj)
	{
		if (pObj != 0)
			((delegate* unmanaged[Stdcall]<nint, uint>)Vtable(pObj)[2])(pObj);
	}

	// ── Public API ──

#pragma warning disable CA1508 // Analyzer can't see through unsafe COM vtable writes

	public static unsafe void OpenAddVoiceDialog()
	{
		_ = CoInitializeEx(0, 0); // COINIT_MULTITHREADED; ignore if already init'd

		nint pAuto = 0, pRoot = 0, pSettings = 0, pAddBtn = 0, pInvoke = 0;

		try
		{
			// Create CUIAutomation
			int hr = CoCreateInstance(
				CLSID_CUIAutomation, 0, 1 /* CLSCTX_INPROC_SERVER */,
				IID_IUIAutomation, out pAuto);
			Marshal.ThrowExceptionForHR(hr);

			// Open Settings → Narrator
			Process.Start
			(
				new ProcessStartInfo("ms-settings:easeofaccess-narrator")
				{
					UseShellExecute = true,
				}
			);

			// Get desktop root element
			var autoVt = Vtable(pAuto);
			hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)
				autoVt[Slot_Auto_GetRootElement])(pAuto, &pRoot);
			Marshal.ThrowExceptionForHR(hr);

			// Poll for Settings window (up to 10 s)
			pSettings = PollForElement
			(
				pAuto, pRoot, TreeScope_Children,
				UIA_NamePropertyId, "Settings", maxAttempts: 50
			);
			if (pSettings == 0)
				return; // Settings didn't appear in UIA -- it's still launching, user can take it from here

			// Bring to foreground
			nint hwnd = 0;
			hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)
				Vtable(pSettings)[Slot_Elem_NativeWindowHandle])(pSettings, &hwnd);
			if (hr == 0 && hwnd != 0)
			{
				ShowWindow(hwnd, SW_RESTORE);
				SetForegroundWindow(hwnd);
			}

			// Poll for the "Add" button (up to 10 s)
			pAddBtn = PollForElement
			(
				pAuto, pSettings, TreeScope_Descendants,
				UIA_AutomationIdPropertyId, AddButtonAutomationId, maxAttempts: 50
			);
			if (pAddBtn == 0)
				return; // Button not found -- Settings is already open, user can navigate manually

			// Get IUIAutomationInvokePattern and invoke
			var iid = IID_IUIAutomationInvokePattern;
			hr = ((delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)
				Vtable(pAddBtn)[Slot_Elem_GetCurrentPatternAs])
				(pAddBtn, UIA_InvokePatternId, &iid, &pInvoke);
			Marshal.ThrowExceptionForHR(hr);

			hr = ((delegate* unmanaged[Stdcall]<nint, int>)
				Vtable(pInvoke)[Slot_Invoke_Invoke])(pInvoke);
			Marshal.ThrowExceptionForHR(hr);
		}
		finally
		{
			Release(pInvoke);
			Release(pAddBtn);
			Release(pSettings);
			Release(pRoot);
			Release(pAuto);
		}
	}

	// ── Internals ──

	/// <summary>
	/// Polls for a UI Automation element matching a string property condition.
	/// Returns the COM pointer to the found element, or 0 if not found.
	/// Caller is responsible for releasing the returned pointer.
	/// </summary>
#pragma warning disable CA1508

	static unsafe nint PollForElement
	(
		nint pAuto,
		nint pParent,
		int scope,
		int propertyId,
		string value,
		int maxAttempts
	)
	{
		var autoVt = Vtable(pAuto);
		var parentVt = Vtable(pParent);

		for (int i = 0; i < maxAttempts; i++)
		{
			Thread.Sleep(200);

			nint bstr = SysAllocString(value);
			var variant = new Variant { vt = VT_BSTR, bstrVal = bstr };
			nint pCond = 0;

			int hr = ((delegate* unmanaged[Stdcall]<nint, int, Variant*, nint*, int>)
				autoVt[Slot_Auto_CreatePropertyCondition])
				(pAuto, propertyId, &variant, &pCond);
			SysFreeString(bstr);

			if (hr != 0 || pCond == 0)
				continue;

			nint pFound = 0;
			hr = ((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)
				parentVt[Slot_Elem_FindFirst])
				(pParent, scope, pCond, &pFound);
			Release(pCond);

			if (hr == 0 && pFound != 0)
				return pFound;
		}

		return 0;
	}
}
