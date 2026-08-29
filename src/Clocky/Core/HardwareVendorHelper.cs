using System;

namespace Clocky.Core;

public record VendorBrand(
    string VendorName,
    string PrimaryColorHex,    // Main badge color (temp / primary)
    string SecondaryColorHex,  // Darker shade for power / secondary
    string TextColorHex,       // High-contrast text color
    string BorderColorHex      // High-contrast border
);

public static class HardwareVendorHelper
{
    // Comprehensive CPU Vendor Detection (11+ Architecture Families)
    public static VendorBrand DetectCpuVendor(string? cpuName)
    {
        string name = (cpuName ?? "").ToLowerInvariant();

        // 1. Intel
        if (name.Contains("intel") || name.Contains("core") || name.Contains("xeon") || 
            name.Contains("i7") || name.Contains("i9") || name.Contains("i5") || name.Contains("i3") || 
            name.Contains("ultra") || name.Contains("pentium") || name.Contains("celeron"))
        {
            return new VendorBrand("Intel", "#0284C7", "#1E40AF", "#FFFFFF", "#38BDF8"); // Intel Electric Blue
        }

        // 2. AMD
        if (name.Contains("amd") || name.Contains("ryzen") || name.Contains("epyc") || 
            name.Contains("threadripper") || name.Contains("athlon") || name.Contains("phenom"))
        {
            return new VendorBrand("AMD", "#EA580C", "#9A3412", "#FFFFFF", "#FB923C"); // AMD Flame Orange
        }

        // 3. Apple Silicon
        if (name.Contains("apple") || name.Contains("m1") || name.Contains("m2") || 
            name.Contains("m3") || name.Contains("m4") || name.Contains("bionic"))
        {
            return new VendorBrand("Apple", "#F1F5F9", "#CBD5E1", "#0F172A", "#64748B"); // Metallic Silver/White with dark charcoal text
        }

        // 4. Qualcomm
        if (name.Contains("qualcomm") || name.Contains("snapdragon") || name.Contains("oryon") || 
            name.Contains("kryo") || name.Contains("sc8") || name.Contains("x elite"))
        {
            return new VendorBrand("Qualcomm", "#DC2626", "#991B1B", "#FFFFFF", "#F87171"); // Snapdragon Crimson Red
        }

        // 5. MediaTek
        if (name.Contains("mediatek") || name.Contains("dimensity") || name.Contains("helio") || name.Contains("kompanio"))
        {
            return new VendorBrand("MediaTek", "#EAB308", "#A16207", "#FFFFFF", "#FDE047"); // MediaTek Gold
        }

        // 6. ARM Native
        if (name.Contains("arm") || name.Contains("cortex") || name.Contains("neoverse"))
        {
            return new VendorBrand("ARM", "#0091BD", "#005F73", "#FFFFFF", "#38BDF8"); // ARM Teal
        }

        // 7. Ampere
        if (name.Contains("ampere") || name.Contains("altra") || name.Contains("emag"))
        {
            return new VendorBrand("Ampere", "#059669", "#064E3B", "#FFFFFF", "#34D399"); // Ampere Emerald
        }

        // 8. Zhaoxin / VIA
        if (name.Contains("zhaoxin") || name.Contains("kaixian") || name.Contains("via") || name.Contains("centaur"))
        {
            return new VendorBrand("Zhaoxin", "#DC2626", "#831843", "#FFFFFF", "#F87171"); // Crimson Ruby
        }

        // 9. RISC-V
        if (name.Contains("risc-v") || name.Contains("sifive") || name.Contains("t-head") || name.Contains("starfive"))
        {
            return new VendorBrand("RISC-V", "#6366F1", "#312E81", "#FFFFFF", "#818CF8"); // RISC-V Indigo
        }

        // 10. IBM
        if (name.Contains("ibm") || name.Contains("power9") || name.Contains("power10") || name.Contains("power8"))
        {
            return new VendorBrand("IBM", "#1E3A8A", "#172554", "#FFFFFF", "#60A5FA"); // IBM Navy
        }

        // 11. Loongson
        if (name.Contains("loongson") || name.Contains("loongarch") || name.Contains("godson"))
        {
            return new VendorBrand("Loongson", "#B91C1C", "#7F1D1D", "#FFFFFF", "#F87171");
        }

        // Generic / Custom
        return new VendorBrand("Generic", "#7289DA", "#5B6EAE", "#FFFFFF", "#7289DA");
    }

    // Comprehensive GPU Vendor Detection (10+ Architecture Families)
    public static VendorBrand DetectGpuVendor(string? gpuName)
    {
        string name = (gpuName ?? "").ToLowerInvariant();

        // 1. NVIDIA
        if (name.Contains("nvidia") || name.Contains("geforce") || name.Contains("rtx") || 
            name.Contains("gtx") || name.Contains("quadro") || name.Contains("tesla") || 
            name.Contains("ada") || name.Contains("hopper") || name.Contains("blackwell"))
        {
            return new VendorBrand("NVIDIA", "#16A34A", "#14532D", "#FFFFFF", "#4ADE80"); // NVIDIA GeForce Green
        }

        // 2. AMD / Radeon
        if (name.Contains("amd") || name.Contains("radeon") || name.Contains("rx ") || 
            name.Contains("vega") || name.Contains("rdna") || name.Contains("instinct") || name.Contains("firepro"))
        {
            return new VendorBrand("AMD", "#DC2626", "#991B1B", "#FFFFFF", "#F87171"); // Radeon Ruby Red
        }

        // 3. Intel Arc / Xe
        if (name.Contains("intel") || name.Contains("arc") || name.Contains("iris") || 
            name.Contains("uhd") || name.Contains("battlemage") || name.Contains("alchemist") || name.Contains("xe "))
        {
            return new VendorBrand("Intel", "#0284C7", "#1E40AF", "#FFFFFF", "#38BDF8"); // Intel Arc Blue
        }

        // 4. Apple Silicon Metal GPU
        if (name.Contains("apple") || name.Contains("m1") || name.Contains("m2") || 
            name.Contains("m3") || name.Contains("m4") || name.Contains("metal"))
        {
            return new VendorBrand("Apple", "#F1F5F9", "#CBD5E1", "#0F172A", "#64748B"); // Apple Silver/White with dark charcoal text
        }

        // 5. Qualcomm Adreno
        if (name.Contains("qualcomm") || name.Contains("adreno") || name.Contains("snapdragon"))
        {
            return new VendorBrand("Qualcomm", "#EA580C", "#9A3412", "#FFFFFF", "#FB923C"); // Adreno Orange
        }

        // 6. MediaTek Immortalis / Mali
        if (name.Contains("immortalis") || (name.Contains("mediatek") && name.Contains("mali")))
        {
            return new VendorBrand("MediaTek", "#EAB308", "#A16207", "#FFFFFF", "#FDE047"); // MediaTek Gold
        }

        // 7. ARM Mali
        if (name.Contains("mali") || name.Contains("valhall") || name.Contains("bifrost"))
        {
            return new VendorBrand("ARM Mali", "#0D9488", "#115E59", "#FFFFFF", "#2DD4BF"); // ARM Teal
        }

        // 8. Imagination PowerVR
        if (name.Contains("powervr") || name.Contains("imagination") || name.Contains("rogu"))
        {
            return new VendorBrand("PowerVR", "#7C3AED", "#4C1D95", "#FFFFFF", "#A78BFA"); // Imagination Purple
        }

        // 9. Moore Threads
        if (name.Contains("moore") || name.Contains("mtt") || name.Contains("chunxiao") || name.Contains("s80") || name.Contains("s70"))
        {
            return new VendorBrand("Moore Threads", "#EC4899", "#9D174D", "#FFFFFF", "#F472B6"); // MTT Magenta
        }

        // 10. Matrox
        if (name.Contains("matrox") || name.Contains("mura") || name.Contains("luma") || name.Contains("millennium"))
        {
            return new VendorBrand("Matrox", "#2563EB", "#1E3A8A", "#FFFFFF", "#60A5FA");
        }

        // Generic Fallback GPU
        return new VendorBrand("Generic", "#16A34A", "#14532D", "#FFFFFF", "#4ADE80");
    }
}
