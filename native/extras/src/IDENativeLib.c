/*
 * libIDENativeLib.so - Android arm64 replacement for IDENativeLib.dll (GameMaker IDE).
 *
 * The Windows DLL exports a single function, DieDieDie (ordinal 1). Disassembly:
 *     mov dword ptr [0], 0x4D2   ; write 1234 to address 0
 *     ret
 * i.e. a deliberate native access violation. It is called only by
 * DebugInfo.dll: YoYoStudio.Plugins.DebugInfo.DebugInfoModule.DebugInfo_Die
 * (managed decl: [DllImport("IDENativeLib")] static extern void DieDieDie();)
 * to test the crash path. Same behaviour here: log, then fault (SIGSEGV),
 * with abort() as a fallback if the null write is somehow survivable.
 */
#include <stdlib.h>
#include <android/log.h>

__attribute__((visibility("default"), noreturn))
void DieDieDie(void)
{
    __android_log_print(ANDROID_LOG_FATAL, "IDENativeLib",
                        "DieDieDie(): deliberate crash requested by DebugInfo_Die");
    *(volatile int *)0 = 1234;
    abort();
}
