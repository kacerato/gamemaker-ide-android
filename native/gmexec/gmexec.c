// gmexec: executavel minimo que a IDE (e o Igor) lancam no lugar do Igor/GMWebServer.
//
// Vai no APK como lib/arm64-v8a/libgmexec.so (EXECUTAVEL PIE com nome de lib: so a
// nativeLibraryDir pode ser executada por apps no Android 10+, W^X). A IDE o lanca com
// Process.Start (CmdProcess, Utils.dll) como ${igor_path}; ele repassa argv/cwd/env ao
// BuildBridge (host/BuildBridge.cs, no servico android:process=":build") por um socket
// unix e devolve stdout/stderr/exit. Assim o CmdProcess da IDE continua com um processo
// de verdade: HasExited, Kill (Stop) e a leitura do stdout funcionam sem patch.
//
// Protocolo (ver BuildBridge.HandleClient):
//   stub -> ponte: "GMX1", int32 argc, argc x str, str cwd, int32 envc, envc x (str k, str v)
//   ponte -> stub: frames { byte 'O'|'E'|'X', int32 len, bytes }; 'X' traz int32 exit code.
//                  'O'/'E' = uma linha (o stub acrescenta '\n'); 'o'/'e' = bytes crus, sem
//                  acrescimo (Language Server: JSON-RPC com Content-Length).
//   stub -> ponte, depois do pedido: frames { byte 'I', int32 len, bytes } com o stdin do stub,
//                  len 0 = EOF. A ponte antiga (so Igor) le e descarta tudo ate o socket fechar.
//   O env leva tambem GMEXEC_ARGV0 = argv[0] (o nome com que o stub foi chamado).
//   str = int32 len + UTF-8. Inteiros little-endian (arm64 e x64).
//   Usado por: Igor/GMWebServer (BuildBridge.cs), ProjectTool e GameMakerLanguageServer
//   (ToolBridge.cs, docs/external-tools.md).
//
// Socket: $GMEXEC_SOCKET ou "@io.gmide.android.exec". '@' no inicio = namespace abstrato
// (Linux/Android); sem '@' = caminho no sistema de arquivos (usado no teste do PC, Windows).
// O servico :build pode estar subindo (ou reiniciando depois de um Environment.Exit do Igor):
// tenta conectar por ate $GMEXEC_CONNECT_TIMEOUT segundos (padrao 30).
// Codigos proprios: 127 = nao conectou; 126 = conexao caiu sem exit code.

#include <errno.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <winsock2.h>
#include <afunix.h>
#include <direct.h>
#include <fcntl.h>
#include <io.h>
#include <windows.h>
typedef SOCKET sock_t;
#define BAD_SOCK INVALID_SOCKET
#define getcwd _getcwd
#define environ _environ
static void sleep_ms(int ms) { Sleep(ms); }
static int sock_err(void) { return WSAGetLastError(); }
static void sock_close(sock_t s) { closesocket(s); }
static int read_stdin(void *buf, int n) { return _read(0, buf, (unsigned)n); }
#else
#include <pthread.h>
#include <signal.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>
extern char **environ;
typedef int sock_t;
#define BAD_SOCK (-1)
static void sleep_ms(int ms) { struct timespec t = { ms / 1000, (long)(ms % 1000) * 1000000L }; nanosleep(&t, NULL); }
static int sock_err(void) { return errno; }
static void sock_close(sock_t s) { close(s); }
static int read_stdin(void *buf, int n) {
  for (;;) {
    ssize_t k = read(0, buf, (size_t)n);
    if (k < 0 && errno == EINTR) continue;
    return (int)k;
  }
}
#endif

#define DEFAULT_SOCKET "@io.gmide.android.exec"

static int wr(sock_t s, const void *p, size_t n) {
  const char *c = (const char *)p;
  while (n) {
    int k = (int)send(s, c, (int)(n > 65536 ? 65536 : n), 0);
    if (k <= 0) return -1;
    c += k; n -= (size_t)k;
  }
  return 0;
}

static int rd(sock_t s, void *p, size_t n) {
  char *c = (char *)p;
  while (n) {
    int k = (int)recv(s, c, (int)(n > 65536 ? 65536 : n), 0);
    if (k <= 0) return -1;
    c += k; n -= (size_t)k;
  }
  return 0;
}

static int wint(sock_t s, int32_t v) { return wr(s, &v, 4); }

static int wstr(sock_t s, const char *t, size_t n) {
  if (wint(s, (int32_t)n)) return -1;
  return n ? wr(s, t, n) : 0;
}

// Repassa o stdin do stub como frames 'I' (o Language Server conversa por stdin/stdout).
// So esta thread escreve no socket depois do pedido; a principal so le.
static sock_t g_sock = BAD_SOCK;

#ifdef _WIN32
static DWORD WINAPI stdin_pump(LPVOID unused)
#else
static void *stdin_pump(void *unused)
#endif
{
  (void)unused;
  char buf[16384];
  for (;;) {
    int k = read_stdin(buf, (int)sizeof buf);
    if (k <= 0) break; // EOF (ou stdin fechado/invalido)
    if (wr(g_sock, "I", 1) || wint(g_sock, k) || wr(g_sock, buf, (size_t)k)) return 0;
  }
  if (wr(g_sock, "I", 1) == 0) wint(g_sock, 0); // EOF
  return 0;
}

static void start_stdin_pump(void) {
#ifdef _WIN32
  HANDLE h = CreateThread(NULL, 0, stdin_pump, NULL, 0, NULL);
  if (h) CloseHandle(h);
#else
  pthread_t t;
  if (pthread_create(&t, NULL, stdin_pump, NULL) == 0) pthread_detach(t);
#endif
}

static sock_t try_connect(const char *name) {
  struct sockaddr_un a;
  memset(&a, 0, sizeof a);
  a.sun_family = AF_UNIX;
  size_t n = strlen(name);
  int len;
  if (name[0] == '@') { // abstrato: sun_path[0] = 0, sem terminador
    if (n > sizeof a.sun_path) return BAD_SOCK;
    memcpy(a.sun_path + 1, name + 1, n - 1);
    len = (int)(offsetof(struct sockaddr_un, sun_path) + n);
  } else {
    if (n >= sizeof a.sun_path) return BAD_SOCK;
    memcpy(a.sun_path, name, n);
    len = (int)sizeof a;
  }
  sock_t s = socket(AF_UNIX, SOCK_STREAM, 0);
  if (s == BAD_SOCK) return BAD_SOCK;
  if (connect(s, (struct sockaddr *)&a, len) != 0) { sock_close(s); return BAD_SOCK; }
  return s;
}

int main(int argc, char **argv) {
#ifdef _WIN32
  WSADATA wsa;
  if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return 127;
  // Bytes crus nos dois sentidos (Content-Length do JSON-RPC conta bytes).
  _setmode(_fileno(stdin), _O_BINARY);
  _setmode(_fileno(stdout), _O_BINARY);
  _setmode(_fileno(stderr), _O_BINARY);
#else
  signal(SIGPIPE, SIG_IGN);
#endif
  const char *name = getenv("GMEXEC_SOCKET");
  if (!name || !*name) name = DEFAULT_SOCKET;
  const char *to = getenv("GMEXEC_CONNECT_TIMEOUT");
  int timeout_ms = (to && *to) ? atoi(to) * 1000 : 30000;

  sock_t s = BAD_SOCK;
  int waited = 0, err = 0;
  for (;;) {
    s = try_connect(name);
    if (s != BAD_SOCK) break;
    err = sock_err();
    if (waited >= timeout_ms) {
      fprintf(stderr, "gmexec: BuildBridge indisponivel em %s (erro %d) apos %d s\n", name, err, waited / 1000);
      return 127;
    }
    sleep_ms(200);
    waited += 200;
  }

  int ok = wr(s, "GMX1", 4) == 0 && wint(s, argc - 1) == 0;
  for (int i = 1; ok && i < argc; i++) ok = wstr(s, argv[i], strlen(argv[i])) == 0;
  char cwd[4096];
  if (ok) {
    const char *c = getcwd(cwd, sizeof cwd) ? cwd : "";
    ok = wstr(s, c, strlen(c)) == 0;
  }
  int32_t envc = 1; // + GMEXEC_ARGV0
  for (char **p = environ; p && *p; p++) envc++;
  if (ok) ok = wint(s, envc) == 0;
  for (char **p = environ; ok && p && *p; p++) {
    const char *eq = strchr(*p, '=');
    if (!eq) ok = wstr(s, *p, strlen(*p)) == 0 && wstr(s, "", 0) == 0;
    else ok = wstr(s, *p, (size_t)(eq - *p)) == 0 && wstr(s, eq + 1, strlen(eq + 1)) == 0;
  }
  if (ok) ok = wstr(s, "GMEXEC_ARGV0", 12) == 0 && wstr(s, argv[0], strlen(argv[0])) == 0;
  if (!ok) { fprintf(stderr, "gmexec: falha ao enviar o pedido\n"); return 126; }
  g_sock = s;
  start_stdin_pump();

  for (;;) {
    uint8_t t;
    int32_t len;
    if (rd(s, &t, 1) || rd(s, &len, 4) || len < 0) break;
    char *buf = (char *)malloc((size_t)len + 1);
    if (!buf || (len && rd(s, buf, (size_t)len))) { free(buf); break; }
    if (t == 'X') {
      int32_t code = 1;
      if (len >= 4) memcpy(&code, buf, 4);
      free(buf);
      fflush(stdout);
      fflush(stderr);
      sock_close(s);
      return code;
    }
    FILE *f = (t == 'E' || t == 'e') ? stderr : stdout;
    fwrite(buf, 1, (size_t)len, f);
    if (t == 'O' || t == 'E') fputc('\n', f); // 'o'/'e': bytes crus
    fflush(f);
    free(buf);
  }
  // Conexao caiu sem 'X': o processo :build morreu (Environment.Exit do Igor/AC, Stop...).
  fflush(stdout);
  fprintf(stderr, "gmexec: conexao com o BuildBridge encerrada sem codigo de saida\n");
  return 126;
}
