// libgmhost.so: ponte entre o SDLActivity (Java) e o lancador em C#.
// O SDL chama SDL_main() na thread dele; aqui repassamos para o ponteiro de
// funcao que o C# registrou em gmhost_set_main() durante Application.OnCreate.
//
// Tambem tem um registrador de crash nativo (gmhost_install_crash_logger): o
// Mono intercepta SIGSEGV/SIGBUS/SIGILL/SIGABRT e, quando a falha e em codigo
// nativo, nao deixa nada util no logcat. Instalado depois do Mono, este handler
// grava no logcat (tag "gmhost-crash") o endereco da falha, a thread, os
// registradores e a pilha (cadeia de frame pointers + dladdr) e depois repassa
// o sinal ao handler anterior (o do Mono), sem mudar o comportamento.
#define _GNU_SOURCE
#include <stddef.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <signal.h>
#include <dlfcn.h>
#include <unistd.h>
#include <ucontext.h>
#include <sys/prctl.h>
#include <sys/syscall.h>
#include <android/log.h>

#define API __attribute__((visibility("default")))
#define TAG "gmhost"
#define CTAG "gmhost-crash"

typedef int (*managed_main_fn)(int argc, char **argv);
static managed_main_fn g_main = NULL;

API void gmhost_set_main(managed_main_fn fn) { g_main = fn; }

API int SDL_main(int argc, char **argv)
{
    if (!g_main) {
        __android_log_print(ANDROID_LOG_ERROR, TAG, "SDL_main: lancador C# nao registrado");
        return 1;
    }
    __android_log_print(ANDROID_LOG_INFO, TAG, "SDL_main -> lancador C# (argc=%d)", argc);
    return g_main(argc, argv);
}

// ---- registrador de crash ---------------------------------------------------

static const int k_signals[] = { SIGSEGV, SIGBUS, SIGILL, SIGFPE, SIGABRT };
#define NSIG_HANDLED (sizeof(k_signals) / sizeof(k_signals[0]))
static struct sigaction g_prev[NSIG_HANDLED];
static volatile int g_in_handler = 0;

static void log_addr(const char *label, int idx, uintptr_t pc)
{
    Dl_info info;
    if (pc && dladdr((void *)pc, &info) && info.dli_fname) {
        const char *lib = strrchr(info.dli_fname, '/');
        lib = lib ? lib + 1 : info.dli_fname;
        uintptr_t off = pc - (uintptr_t)info.dli_fbase;
        if (info.dli_sname)
            __android_log_print(ANDROID_LOG_FATAL, CTAG, "%s #%02d pc %016lx  %s+0x%lx (%s+0x%lx)",
                                label, idx, (unsigned long)pc, lib, (unsigned long)off, info.dli_sname,
                                (unsigned long)(pc - (uintptr_t)info.dli_saddr));
        else
            __android_log_print(ANDROID_LOG_FATAL, CTAG, "%s #%02d pc %016lx  %s+0x%lx",
                                label, idx, (unsigned long)pc, lib, (unsigned long)off);
    } else {
        // Codigo do JIT do Mono: pergunta o nome do metodo gerenciado.
        typedef char *(*pmip_fn)(void *ip);
        static pmip_fn pmip = NULL;
        if (!pmip) pmip = (pmip_fn)dlsym(RTLD_DEFAULT, "mono_pmip");
        char *name = (pc && pmip) ? pmip((void *)pc) : NULL;
        if (name) {
            __android_log_print(ANDROID_LOG_FATAL, CTAG, "%s #%02d pc %016lx  [gerenciado] %s",
                                label, idx, (unsigned long)pc, name);
            return;
        }
        // Ultimo recurso: /proc/self/maps diz qual arquivo esta mapeado nesse endereco.
        char mapline[512] = "(sem mapeamento)";
        FILE *f = fopen("/proc/self/maps", "r");
        if (f) {
            char line[512];
            while (fgets(line, sizeof(line), f)) {
                unsigned long lo, hi, off;
                if (sscanf(line, "%lx-%lx %*s %lx", &lo, &hi, &off) == 3 && pc >= lo && pc < hi) {
                    line[strcspn(line, "\n")] = 0;
                    snprintf(mapline, sizeof(mapline), "%s  (+0x%lx no mapeamento)", line, (unsigned long)(pc - lo + off));
                    break;
                }
            }
            fclose(f);
        }
        __android_log_print(ANDROID_LOG_FATAL, CTAG, "%s #%02d pc %016lx  %s", label, idx, (unsigned long)pc, mapline);
    }
}

static void crash_handler(int sig, siginfo_t *si, void *uc_void)
{
    int idx = -1;
    for (size_t i = 0; i < NSIG_HANDLED; i++) if (k_signals[i] == sig) idx = (int)i;

    if (!g_in_handler) {
        g_in_handler = 1;
        char tname[17] = {0};
        prctl(PR_GET_NAME, tname, 0, 0, 0);
        __android_log_print(ANDROID_LOG_FATAL, CTAG, "sinal %d code %d fault addr %p, tid %ld (%s)",
                            sig, si ? si->si_code : 0, si ? si->si_addr : NULL, (long)syscall(SYS_gettid), tname);
#if defined(__aarch64__)
        ucontext_t *uc = (ucontext_t *)uc_void;
        if (uc) {
            uintptr_t pc = uc->uc_mcontext.pc, lr = uc->uc_mcontext.regs[30], fp = uc->uc_mcontext.regs[29];
            __android_log_print(ANDROID_LOG_FATAL, CTAG, "x0 %016llx x1 %016llx x2 %016llx x3 %016llx",
                                uc->uc_mcontext.regs[0], uc->uc_mcontext.regs[1], uc->uc_mcontext.regs[2], uc->uc_mcontext.regs[3]);
            __android_log_print(ANDROID_LOG_FATAL, CTAG, "sp %016llx fp %016lx lr %016lx",
                                uc->uc_mcontext.sp, (unsigned long)fp, (unsigned long)lr);
            log_addr("pc", 0, pc);
            log_addr("lr", 1, lr);
            // Cadeia de frame pointers: [fp] = fp anterior, [fp+8] = endereco de retorno.
            for (int i = 2; i < 40 && fp && (fp & 7) == 0; i++) {
                uintptr_t *frame = (uintptr_t *)fp;
                uintptr_t ret = frame[1], next = frame[0];
                if (!ret) break;
                log_addr("fp", i, ret);
                if (next <= fp) break;
                fp = next;
            }
        }
#endif
        g_in_handler = 0;
    }

    // Repassa ao handler anterior (Mono), que decide se e NullReferenceException.
    if (idx >= 0) {
        struct sigaction *prev = &g_prev[idx];
        if (prev->sa_flags & SA_SIGINFO) {
            if (prev->sa_sigaction) { prev->sa_sigaction(sig, si, uc_void); return; }
        } else if (prev->sa_handler == SIG_IGN) {
            return;
        } else if (prev->sa_handler != SIG_DFL && prev->sa_handler) {
            prev->sa_handler(sig);
            return;
        }
        // Sem handler anterior: comportamento padrao (gera tombstone).
        signal(sig, SIG_DFL);
        raise(sig);
    }
}

API int gmhost_install_crash_logger(void)
{
    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_sigaction = crash_handler;
    sa.sa_flags = SA_SIGINFO | SA_ONSTACK;
    sigemptyset(&sa.sa_mask);
    int ok = 0;
    for (size_t i = 0; i < NSIG_HANDLED; i++)
        if (sigaction(k_signals[i], &sa, &g_prev[i]) == 0) ok++;
    __android_log_print(ANDROID_LOG_INFO, TAG, "registrador de crash instalado (%d sinais)", ok);
    return ok;
}
