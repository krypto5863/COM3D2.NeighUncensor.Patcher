using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;

namespace COM3D2.NeighUncensor.Patcher
{
    public static class NeighUncensorPatcher
    {
        public static readonly string[] TargetAssemblyNames = { "Assembly-CSharp.dll" };

        public static void Patch(AssemblyDefinition ad)
        {
            var hookAd = AssemblyDefinition.ReadAssembly(Assembly.GetExecutingAssembly().Location);
            var hooks = hookAd.MainModule.GetType("COM3D2.NeighUncensor.Patcher.Hooks");
            var gameMain = ad.MainModule.GetType("GameMain");
            var onInit = gameMain.Methods.FirstOrDefault(m => m.Name == "OnInitialize");
            var ins = onInit.Body.Instructions.First();
            var il = onInit.Body.GetILProcessor();
            il.InsertBefore(
                ins,
                il.Create(OpCodes.Call,
                          ad.MainModule.ImportReference(
                              hooks.Methods.FirstOrDefault(m => m.Name == nameof(Hooks.FixShaders)))));
        }
    }

    public static class Hooks
    {
        // List of shaders the RQ of which to zero
        private static readonly string[] ShadersToCleanup = { "CM3D2/Mosaic", "CM3D2/Mosaic_en" };

        // We need to keep at least one instance of the shader alive so that it won't get unloaded
        private static readonly List<object> ShadersCache = new List<object>();

		public static void FixShaders()
        {
			try
            {
                var modules = Process.GetCurrentProcess().Modules;

                ProcessModule mono = null;
                foreach (ProcessModule module in modules)
                {
                    if (!module.ModuleName.ToLowerInvariant().Contains("mono"))
                        continue;

                    if (GetProcAddress(module.BaseAddress, "mono_add_internal_call") == IntPtr.Zero)
                        continue;

                    mono = module;
                }

                if (mono == null)
                {
	                throw new NullReferenceException("The mono module is null.");
                }

                var addICall =
                    Marshal.GetDelegateForFunctionPointer(GetProcAddress(mono.BaseAddress, "mono_add_internal_call"),
                                                          typeof(AddICallDelegate)) as AddICallDelegate;
                
                addICall("COM3D2.NeighUncensor.Patcher.Hooks::FixShader",
                         Marshal.GetFunctionPointerForDelegate(new FixShaderDelegate(FixShaderImpl)));

				CleanupMosaicShaders();
            }
            catch (Exception e)
            {
                File.WriteAllText("neigh_uncensor_error.log", e.ToString());
            }
        }

        private static void CleanupMosaicShaders()
        {
            foreach (string shaderName in ShadersToCleanup)
            {
                var s = Shader.Find(shaderName);
                if (s == null)
                    continue;

                ShadersCache.Add(s);
                FixShader(s);
            }
        }

        public static unsafe void FixShaderImpl(IntPtr shaderObj)
        {
            var s = (byte*)shaderObj.ToPointer();
            var a = *(byte**)(s + 0x10);
            var b = *(byte**)(a + 0x38);
            var rq = (uint*)(b + 0x5C);
            *rq = 0u;
        }

        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void FixShader(Shader s);

        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        private delegate void FixShaderDelegate(IntPtr shader);

        private delegate void AddICallDelegate([MarshalAs(UnmanagedType.LPStr)] string name, IntPtr addr);
    }
}