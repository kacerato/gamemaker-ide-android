// Reimplementacao minima da yyal (audio da YoYo) para Android arm64.
//
// Codigo proprio: nao contem codigo da YoYo. As assinaturas das 47 funcoes
// exportadas vem das declaracoes P/Invoke de
// YoYoStudio.Plugins.Audio.Internal.NativeMethods (Audio.dll 2026.0.0.16) e
// todas devolvem ErrorCode (int32): None, Crit, Init, Ptr, Op, Idx, Inst, Val, Done.
//
// O que funciona (o que o editor de sons da IDE usa):
//   - saida AAudio float estereo, mixer de varias vozes com reamostragem linear;
//   - StreamCreate/StreamFree: decodifica o arquivo inteiro para PCM int16 na
//     memoria (WAV via dr_wav, Ogg Vorbis via stb_vorbis, MP3 via dr_mp3);
//   - AudioPlay/Pause/Resume/Stop/IsPlaying/IsPaused/Exists, gain com rampa
//     em ms, loop, posicao e duracao em segundos, pitch;
//   - fila de eventos de fim de reproducao (PopEndOfPlaybackEvent);
//   - emissores: so gain e pitch (multiplicam os das vozes ligadas a eles).
// No-op: grupos, buses, efeitos, listener, modelo de falloff, posicao 3D,
// prioridade, listenerMask, e os callbacks de alocador/arquivo/log/mutex/thread
// da IDE (usamos malloc, stdio, __android_log_print e pthread proprios).
//
// Handles: um contador unico para streams, vozes e emissores, de modo que
// AudioExists/AudioGetDuration aceitam tanto handle de stream (a IDE chama
// AudioExists(assetHandle) para esperar a liberacao do arquivo e
// AudioGetDuration(assetHandle) para a duracao do som) quanto de voz.
//
// Getters/setters de voz com handle inexistente devolvem None com valor
// padrao (a voz pode ter acabado entre duas chamadas da IDE; um erro faria a
// IDE logar a cada consulta). AudioPlay com asset invalido devolve Idx.
#include <stdint.h>
#include <stddef.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <stdarg.h>
#include <math.h>
#include <pthread.h>
#include <aaudio/AAudio.h>
#include <android/log.h>

#define STB_VORBIS_NO_PUSHDATA_API
#include "third_party/stb_vorbis.c"
#define DR_WAV_IMPLEMENTATION
#include "third_party/dr_wav.h"
#define DR_MP3_IMPLEMENTATION
#include "third_party/dr_mp3.h"

#define API __attribute__((visibility("default")))
typedef int32_t yyal_err;

enum { E_NONE, E_CRIT, E_INIT, E_PTR, E_OP, E_IDX, E_INST, E_VAL, E_DONE };

// ---------------------------------------------------------------------------
// Layouts das structs da IDE (Marshal.StructureToPtr, LayoutKind.Sequential
// implicito, alinhamento natural, bool -> BOOL de 4 bytes):
//
// YoYoStudio.Plugins.Audio.Internal.PlaybackConfiguration (80 bytes)
//   off  0  int32  type          PositionType: 0 NonPositional,
//                                1 PositionalSpecified, 2 PositionalEmitter
//   off  4  int32  assetHandle   handle devolvido por StreamCreate
//   off  8  double priority
//   off 16  int32  loop          (bool)
//   off 20  ----   padding
//   off 24  double gain
//   off 32  double offset        posicao inicial em segundos
//   off 40  double pitch
//   off 48  int32  listenerMask  (a IDE sempre passa 1)
//   off 52  Position position    { Vec3 vec {float x,y,z}; float falloffRef,
//                                  falloffMax, falloffFac; } = 24 bytes
//   off 76  int32  emitterHandle -1 = sem emissor
//
// YoYoStudio.Plugins.Audio.Internal.EndOfPlaybackEvent (12 bytes)
//   off 0 int32 instHandle; off 4 int32 assetHandle; off 8 int32 wasStopped
//
// A IDE chama PopEndOfPlaybackEvent em loop no Tick ate o retorno ser != None;
// Done = fila vazia (Result.Check nao loga Done).
// ---------------------------------------------------------------------------
typedef struct {
    int32_t type;
    int32_t assetHandle;
    double priority;
    int32_t loop;
    int32_t pad0;
    double gain;
    double offset;
    double pitch;
    int32_t listenerMask;
    float pos_x, pos_y, pos_z, falloffRef, falloffMax, falloffFac;
    int32_t emitterHandle;
} PlayConfig;
_Static_assert(sizeof(PlayConfig) == 80, "PlaybackConfiguration tem 80 bytes");
_Static_assert(offsetof(PlayConfig, emitterHandle) == 76, "emitterHandle em 76");

typedef struct { int32_t instHandle, assetHandle, wasStopped; } EndEvent;
_Static_assert(sizeof(EndEvent) == 12, "EndOfPlaybackEvent tem 12 bytes");

// ---------------------------------------------------------------------------
#define MAX_STREAMS 512
#define MAX_VOICES 64
#define MAX_EMITTERS 128
#define EVQ_SIZE 256
#define OUT_CH 2

typedef struct {
    int32_t id;          // 0 = slot livre
    int16_t *pcm;        // intercalado
    int channels;
    int rate;
    uint64_t frames;
} Stream;

typedef struct {
    int32_t id;          // 0 = slot livre
    Stream *s;
    int32_t emitter;
    double pos;          // em frames do arquivo
    double pitch;
    float gain, gain_target, gain_step; // gain_step por frame de saida
    int32_t gain_frames; // frames restantes da rampa
    int loop, paused;
} Voice;

typedef struct { int32_t id; float gain, pitch; } Emitter;

static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;   // estado do mixer
static pthread_mutex_t g_dev_lock = PTHREAD_MUTEX_INITIALIZER; // abrir/fechar AAudio
static Stream g_streams[MAX_STREAMS];
static Voice g_voices[MAX_VOICES];
static Emitter g_emitters[MAX_EMITTERS];
static EndEvent g_evq[EVQ_SIZE];
static int g_evq_head, g_evq_count;
static int32_t g_next_handle = 1;
static AAudioStream *g_out;
static int g_out_rate = 48000;
static volatile int g_need_restart;
static int g_inited;
static char g_errmsg[256] = "";

#define LOG_TAG "yyal"
static void logf_(int prio, const char *fmt, ...)
{
    va_list ap; va_start(ap, fmt);
    __android_log_vprint(prio, LOG_TAG, fmt, ap);
    va_end(ap);
}

static yyal_err fail(yyal_err e, const char *fmt, ...)
{
    va_list ap; va_start(ap, fmt);
    vsnprintf(g_errmsg, sizeof g_errmsg, fmt, ap);
    va_end(ap);
    logf_(ANDROID_LOG_WARN, "%s", g_errmsg);
    return e;
}

static int32_t new_handle(void) { int32_t h = g_next_handle++; if (g_next_handle <= 0) g_next_handle = 1; return h; }

// Chamar com g_lock.
static Stream *find_stream(int32_t id)
{
    if (id <= 0) return NULL;
    for (int i = 0; i < MAX_STREAMS; i++) if (g_streams[i].id == id) return &g_streams[i];
    return NULL;
}
static Voice *find_voice(int32_t id)
{
    if (id <= 0) return NULL;
    for (int i = 0; i < MAX_VOICES; i++) if (g_voices[i].id == id) return &g_voices[i];
    return NULL;
}
static Emitter *find_emitter(int32_t id)
{
    if (id <= 0) return NULL;
    for (int i = 0; i < MAX_EMITTERS; i++) if (g_emitters[i].id == id) return &g_emitters[i];
    return NULL;
}

static void push_event(int32_t inst, int32_t asset, int stopped)
{
    if (g_evq_count == EVQ_SIZE) { g_evq_head = (g_evq_head + 1) % EVQ_SIZE; g_evq_count--; } // descarta o mais antigo
    g_evq[(g_evq_head + g_evq_count) % EVQ_SIZE] = (EndEvent){ inst, asset, stopped };
    g_evq_count++;
}

static void end_voice(Voice *v, int stopped)
{
    push_event(v->id, v->s ? v->s->id : -1, stopped);
    memset(v, 0, sizeof *v);
}

// ---------------------------------------------------------------------------
// Mixer (thread de audio do AAudio)
static void mix(float *out, int32_t nframes)
{
    memset(out, 0, sizeof(float) * OUT_CH * (size_t)nframes);
    for (int vi = 0; vi < MAX_VOICES; vi++) {
        Voice *v = &g_voices[vi];
        if (!v->id || v->paused || !v->s) continue;
        Stream *s = v->s;
        Emitter *em = find_emitter(v->emitter);
        double step = (double)s->rate / g_out_rate * v->pitch * (em ? em->pitch : 1.0f);
        float egain = em ? em->gain : 1.0f;
        const int ch = s->channels;
        const double frames = (double)s->frames;
        int ended = 0;
        for (int32_t f = 0; f < nframes; f++) {
            if (v->pos >= frames) {
                if (v->loop && frames > 0) v->pos = fmod(v->pos, frames);
                else { ended = 1; break; }
            }
            uint64_t i0 = (uint64_t)v->pos;
            uint64_t i1 = i0 + 1;
            if (i1 >= s->frames) i1 = v->loop ? 0 : i0;
            float t = (float)(v->pos - (double)i0);
            const int16_t *a = s->pcm + i0 * ch, *b = s->pcm + i1 * ch;
            float l = (a[0] + (b[0] - a[0]) * t) * (1.0f / 32768.0f);
            float r = ch > 1 ? (a[1] + (b[1] - a[1]) * t) * (1.0f / 32768.0f) : l;
            if (v->gain_frames > 0) {
                v->gain += v->gain_step;
                if (--v->gain_frames == 0) v->gain = v->gain_target;
            }
            float g = v->gain * egain;
            out[f * 2] += l * g;
            out[f * 2 + 1] += r * g;
            v->pos += step;
        }
        if (ended) end_voice(v, 0);
    }
    for (int32_t i = 0; i < nframes * OUT_CH; i++) {
        float x = out[i];
        out[i] = x > 1.0f ? 1.0f : (x < -1.0f ? -1.0f : x);
    }
}

static aaudio_data_callback_result_t data_cb(AAudioStream *st, void *user, void *audio, int32_t nframes)
{
    (void)st; (void)user;
    pthread_mutex_lock(&g_lock);
    mix((float *)audio, nframes);
    pthread_mutex_unlock(&g_lock);
    return AAUDIO_CALLBACK_RESULT_CONTINUE;
}

static void error_cb(AAudioStream *st, void *user, aaudio_result_t err)
{
    (void)st; (void)user;
    // Nao se pode fechar o stream aqui; o Tick (thread da IDE) reabre.
    logf_(ANDROID_LOG_WARN, "AAudio erro %d (%s); reabrindo no proximo Tick", err, AAudio_convertResultToText(err));
    g_need_restart = 1;
}

// Chamar com g_dev_lock e SEM g_lock (close espera o callback terminar).
static void device_close(void)
{
    if (!g_out) return;
    AAudioStream_requestStop(g_out);
    AAudioStream_close(g_out);
    g_out = NULL;
}

static int device_open(void)
{
    AAudioStreamBuilder *b = NULL;
    aaudio_result_t r = AAudio_createStreamBuilder(&b);
    if (r != AAUDIO_OK) { fail(E_INIT, "AAudio_createStreamBuilder: %s", AAudio_convertResultToText(r)); return 0; }
    AAudioStreamBuilder_setDirection(b, AAUDIO_DIRECTION_OUTPUT);
    AAudioStreamBuilder_setFormat(b, AAUDIO_FORMAT_PCM_FLOAT);
    AAudioStreamBuilder_setChannelCount(b, OUT_CH);
    AAudioStreamBuilder_setSharingMode(b, AAUDIO_SHARING_MODE_SHARED);
    AAudioStreamBuilder_setPerformanceMode(b, AAUDIO_PERFORMANCE_MODE_NONE);
    AAudioStreamBuilder_setDataCallback(b, data_cb, NULL);
    AAudioStreamBuilder_setErrorCallback(b, error_cb, NULL);
    AAudioStream *st = NULL;
    r = AAudioStreamBuilder_openStream(b, &st);
    AAudioStreamBuilder_delete(b);
    if (r != AAUDIO_OK) { fail(E_INIT, "AAudio openStream: %s", AAudio_convertResultToText(r)); return 0; }
    if (AAudioStream_getFormat(st) != AAUDIO_FORMAT_PCM_FLOAT || AAudioStream_getChannelCount(st) != OUT_CH) {
        AAudioStream_close(st);
        fail(E_INIT, "AAudio nao aceitou float estereo");
        return 0;
    }
    int rate = AAudioStream_getSampleRate(st);
    pthread_mutex_lock(&g_lock);
    g_out_rate = rate > 0 ? rate : 48000;
    pthread_mutex_unlock(&g_lock);
    r = AAudioStream_requestStart(st);
    if (r != AAUDIO_OK) { AAudioStream_close(st); fail(E_INIT, "AAudio requestStart: %s", AAudio_convertResultToText(r)); return 0; }
    g_out = st;
    g_need_restart = 0;
    logf_(ANDROID_LOG_INFO, "AAudio aberto: %d Hz, float estereo", g_out_rate);
    return 1;
}

// Garante dispositivo aberto (reabre se desconectou). Sem g_lock.
static void device_ensure(void)
{
    pthread_mutex_lock(&g_dev_lock);
    if (g_inited) {
        if (g_need_restart) device_close();
        if (!g_out) device_open();
    }
    pthread_mutex_unlock(&g_dev_lock);
}

// ---------------------------------------------------------------------------
// Decodificacao
typedef enum { FMT_UNKNOWN, FMT_WAV, FMT_OGG, FMT_MP3 } FileFmt;

static FileFmt sniff(const char *path)
{
    unsigned char h[12] = {0};
    FILE *f = fopen(path, "rb");
    if (!f) return FMT_UNKNOWN;
    size_t n = fread(h, 1, sizeof h, f);
    fclose(f);
    if (n >= 4 && (!memcmp(h, "RIFF", 4) || !memcmp(h, "RF64", 4) || !memcmp(h, "riff", 4))) return FMT_WAV;
    if (n >= 4 && !memcmp(h, "OggS", 4)) return FMT_OGG;
    if (n >= 3 && !memcmp(h, "ID3", 3)) return FMT_MP3;
    if (n >= 2 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0) return FMT_MP3;
    return FMT_UNKNOWN;
}

static int decode(const char *path, FileFmt fmt, int16_t **pcm, int *ch, int *rate, uint64_t *frames)
{
    *pcm = NULL;
    if (fmt == FMT_WAV) {
        unsigned int c = 0, r = 0; drwav_uint64 n = 0;
        drwav_int16 *p = drwav_open_file_and_read_pcm_frames_s16(path, &c, &r, &n, NULL);
        if (!p) return 0;
        // copia para malloc para liberar tudo com free()
        int16_t *q = malloc((size_t)n * c * sizeof(int16_t));
        if (q) memcpy(q, p, (size_t)n * c * sizeof(int16_t));
        drwav_free(p, NULL);
        if (!q) return 0;
        *pcm = q; *ch = (int)c; *rate = (int)r; *frames = n;
        return 1;
    }
    if (fmt == FMT_OGG) {
        int c = 0, r = 0; short *p = NULL;
        int n = stb_vorbis_decode_filename(path, &c, &r, &p);
        if (n < 0 || !p) { free(p); return 0; }
        *pcm = p; *ch = c; *rate = r; *frames = (uint64_t)n;
        return 1;
    }
    if (fmt == FMT_MP3) {
        drmp3_config cfg = {0}; drmp3_uint64 n = 0;
        drmp3_int16 *p = drmp3_open_file_and_read_pcm_frames_s16(path, &cfg, &n, NULL);
        if (!p) return 0;
        int16_t *q = malloc((size_t)n * cfg.channels * sizeof(int16_t));
        if (q) memcpy(q, p, (size_t)n * cfg.channels * sizeof(int16_t));
        drmp3_free(p, NULL);
        if (!q) return 0;
        *pcm = q; *ch = (int)cfg.channels; *rate = (int)cfg.sampleRate; *frames = n;
        return 1;
    }
    return 0;
}

// ---------------------------------------------------------------------------
// Configuracao (callbacks da IDE ignorados)
API yyal_err YYAL_SetAllocatorFunctions(intptr_t p) { (void)p; return E_NONE; }
API yyal_err YYAL_SetFilesystemFunctions(intptr_t p) { (void)p; return E_NONE; }
API yyal_err YYAL_SetLogFunctions(void *p) { (void)p; return E_NONE; }
API yyal_err YYAL_SetMutexFunctions(intptr_t p) { (void)p; return E_NONE; }
API yyal_err YYAL_SetThreadFunctions(intptr_t p) { (void)p; return E_NONE; }

API yyal_err YYAL_Init(void)
{
    pthread_mutex_lock(&g_dev_lock);
    g_inited = 1;
    // Se o AAudio falhar, a IDE continua; tentamos de novo no Tick/AudioPlay.
    if (!g_out) device_open();
    pthread_mutex_unlock(&g_dev_lock);
    return E_NONE;
}

API yyal_err YYAL_Quit(void)
{
    pthread_mutex_lock(&g_dev_lock);
    device_close();
    g_inited = 0;
    pthread_mutex_unlock(&g_dev_lock);
    pthread_mutex_lock(&g_lock);
    memset(g_voices, 0, sizeof g_voices);
    for (int i = 0; i < MAX_STREAMS; i++) free(g_streams[i].pcm);
    memset(g_streams, 0, sizeof g_streams);
    memset(g_emitters, 0, sizeof g_emitters);
    g_evq_head = g_evq_count = 0;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_Tick(void)
{
    if (g_need_restart) device_ensure();
    return E_NONE;
}

API const char *YYAL_GetErrorMsg(void) { return g_errmsg; }
API yyal_err YYAL_EngineSetFalloffModel(int32_t model) { (void)model; return E_NONE; }

// ---------------------------------------------------------------------------
// Streams (assets)
// codec: enum Codec da IDE { Wav = 0, Vorbis = 1 }, escolhido so pela
// extensao (".wav" -> Wav, qualquer outra -> Vorbis). Por isso o formato real
// e detectado pelos bytes iniciais; o codec so serve de palpite.
API yyal_err YYAL_StreamCreate(const char *path, int32_t codec, int32_t *out)
{
    if (!path || !out) return fail(E_PTR, "StreamCreate: ponteiro nulo");
    FileFmt fmt = sniff(path);
    if (fmt == FMT_UNKNOWN) fmt = codec == 0 ? FMT_WAV : FMT_OGG;
    int16_t *pcm = NULL; int ch = 0, rate = 0; uint64_t frames = 0;
    if (!decode(path, fmt, &pcm, &ch, &rate, &frames)) {
        // ultimo recurso: tenta os outros formatos
        static const FileFmt all[] = { FMT_WAV, FMT_OGG, FMT_MP3 };
        int ok = 0;
        for (int i = 0; i < 3 && !ok; i++) if (all[i] != fmt) ok = decode(path, all[i], &pcm, &ch, &rate, &frames);
        if (!ok) return fail(E_OP, "StreamCreate: nao consegui decodificar %s", path);
    }
    if (ch < 1 || rate <= 0) { free(pcm); return fail(E_VAL, "StreamCreate: formato invalido em %s", path); }
    pthread_mutex_lock(&g_lock);
    Stream *s = NULL;
    for (int i = 0; i < MAX_STREAMS && !s; i++) if (!g_streams[i].id) s = &g_streams[i];
    if (!s) { pthread_mutex_unlock(&g_lock); free(pcm); return fail(E_CRIT, "StreamCreate: limite de streams"); }
    s->id = new_handle();
    s->pcm = pcm; s->channels = ch; s->rate = rate; s->frames = frames;
    *out = s->id;
    pthread_mutex_unlock(&g_lock);
    logf_(ANDROID_LOG_INFO, "stream %d: %s (%d ch, %d Hz, %llu frames)", *out, path, ch, rate, (unsigned long long)frames);
    return E_NONE;
}

// Para as vozes que usam o stream e libera a memoria na hora (sob g_lock o
// callback de audio nao esta no meio do mix), entao force/awaitRelease nao
// muda nada: depois do retorno AudioExists(handle) ja e false.
API yyal_err YYAL_StreamFree(int32_t id, int32_t force)
{
    (void)force;
    pthread_mutex_lock(&g_lock);
    Stream *s = find_stream(id);
    if (!s) { pthread_mutex_unlock(&g_lock); return fail(E_IDX, "StreamFree: stream %d inexistente", id); }
    for (int i = 0; i < MAX_VOICES; i++) if (g_voices[i].id && g_voices[i].s == s) end_voice(&g_voices[i], 1);
    free(s->pcm);
    memset(s, 0, sizeof *s);
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

// ---------------------------------------------------------------------------
// Vozes
API yyal_err YYAL_AudioExists(int32_t id, int32_t *exists)
{
    if (!exists) return E_PTR;
    pthread_mutex_lock(&g_lock);
    *exists = (find_voice(id) || find_stream(id)) ? 1 : 0;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_AudioGetDuration(int32_t id, float *v)
{
    if (!v) return E_PTR;
    pthread_mutex_lock(&g_lock);
    Voice *vo = find_voice(id);
    Stream *s = vo ? vo->s : find_stream(id);
    *v = s ? (float)((double)s->frames / s->rate) : 0.0f;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_AudioGetGain(int32_t id, float *v)
{
    if (!v) return E_PTR;
    pthread_mutex_lock(&g_lock);
    Voice *vo = find_voice(id);
    *v = vo ? vo->gain : 1.0f;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_AudioSetGain(int32_t id, float gain, int32_t ms)
{
    if (gain < 0.0f || gain != gain) gain = 0.0f;
    pthread_mutex_lock(&g_lock);
    Voice *vo = find_voice(id);
    if (vo) {
        int32_t n = ms > 0 ? (int32_t)((int64_t)ms * g_out_rate / 1000) : 0;
        vo->gain_target = gain;
        if (n <= 0) { vo->gain = gain; vo->gain_frames = 0; vo->gain_step = 0; }
        else { vo->gain_frames = n; vo->gain_step = (gain - vo->gain) / (float)n; }
    }
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_AudioGetPlaybackPosition(int32_t id, float *v)
{
    if (!v) return E_PTR;
    pthread_mutex_lock(&g_lock);
    Voice *vo = find_voice(id);
    *v = (vo && vo->s) ? (float)(vo->pos / vo->s->rate) : 0.0f;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

static void set_pos(Voice *vo, double secs)
{
    double f = secs * vo->s->rate;
    double max = (double)vo->s->frames;
    if (f < 0 || f != f) f = 0;
    if (f > max) f = max;
    vo->pos = f;
}

API yyal_err YYAL_AudioSetPlaybackPosition(int32_t id, float pos)
{
    pthread_mutex_lock(&g_lock);
    Voice *vo = find_voice(id);
    if (vo && vo->s) set_pos(vo, pos);
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_AudioPlay(intptr_t params, int32_t *out)
{
    if (!params || !out) return fail(E_PTR, "AudioPlay: ponteiro nulo");
    PlayConfig cfg;
    memcpy(&cfg, (const void *)params, sizeof cfg);
    device_ensure();
    pthread_mutex_lock(&g_lock);
    Stream *s = find_stream(cfg.assetHandle);
    if (!s) { pthread_mutex_unlock(&g_lock); return fail(E_IDX, "AudioPlay: asset %d inexistente", cfg.assetHandle); }
    Voice *vo = NULL;
    for (int i = 0; i < MAX_VOICES && !vo; i++) if (!g_voices[i].id) vo = &g_voices[i];
    if (!vo) { pthread_mutex_unlock(&g_lock); return fail(E_CRIT, "AudioPlay: limite de %d vozes", MAX_VOICES); }
    memset(vo, 0, sizeof *vo);
    vo->id = new_handle();
    vo->s = s;
    vo->emitter = cfg.emitterHandle;
    vo->loop = cfg.loop != 0;
    vo->gain = vo->gain_target = (cfg.gain >= 0 && cfg.gain == cfg.gain) ? (float)cfg.gain : 1.0f;
    vo->pitch = (cfg.pitch > 0 && cfg.pitch == cfg.pitch) ? cfg.pitch : 1.0;
    set_pos(vo, cfg.offset);
    *out = vo->id;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

static yyal_err set_paused(int32_t id, int p)
{
    pthread_mutex_lock(&g_lock);
    Voice *vo = find_voice(id);
    if (vo) vo->paused = p;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}
API yyal_err YYAL_AudioPause(int32_t id) { return set_paused(id, 1); }
API yyal_err YYAL_AudioResume(int32_t id) { return set_paused(id, 0); }

API yyal_err YYAL_AudioStop(int32_t id)
{
    pthread_mutex_lock(&g_lock);
    Voice *vo = find_voice(id);
    if (vo) end_voice(vo, 1);
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_AudioIsPlaying(int32_t id, int32_t *v)
{
    if (!v) return E_PTR;
    pthread_mutex_lock(&g_lock);
    Voice *vo = find_voice(id);
    *v = (vo && !vo->paused) ? 1 : 0;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_AudioIsPaused(int32_t id, int32_t *v)
{
    if (!v) return E_PTR;
    pthread_mutex_lock(&g_lock);
    Voice *vo = find_voice(id);
    *v = (vo && vo->paused) ? 1 : 0;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_LoopGetState(int32_t id, int32_t *v)
{
    if (!v) return E_PTR;
    pthread_mutex_lock(&g_lock);
    Voice *vo = find_voice(id);
    *v = (vo && vo->loop) ? 1 : 0;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_LoopSetState(int32_t id, int32_t on)
{
    pthread_mutex_lock(&g_lock);
    Voice *vo = find_voice(id);
    if (vo) vo->loop = on != 0;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

// ---------------------------------------------------------------------------
// Grupos e listener: no-op
API yyal_err YYAL_GroupCreate(const char *name, const char *path, int32_t *out)
{
    (void)name; (void)path;
    if (!out) return E_PTR;
    pthread_mutex_lock(&g_lock); *out = new_handle(); pthread_mutex_unlock(&g_lock);
    return E_NONE;
}
API yyal_err YYAL_GroupDestroy(int32_t id) { (void)id; return E_NONE; }

API yyal_err YYAL_ListenerSetOrientation(int32_t id, double lx, double ly, double lz, double ux, double uy, double uz)
{ (void)id; (void)lx; (void)ly; (void)lz; (void)ux; (void)uy; (void)uz; return E_NONE; }

// ---------------------------------------------------------------------------
// Emissores: so gain e pitch tem efeito
API yyal_err YYAL_EmitterCreate(int32_t *out)
{
    if (!out) return E_PTR;
    pthread_mutex_lock(&g_lock);
    Emitter *e = NULL;
    for (int i = 0; i < MAX_EMITTERS && !e; i++) if (!g_emitters[i].id) e = &g_emitters[i];
    if (!e) { pthread_mutex_unlock(&g_lock); return fail(E_CRIT, "EmitterCreate: limite de emissores"); }
    e->id = new_handle(); e->gain = 1.0f; e->pitch = 1.0f;
    *out = e->id;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_EmitterFree(int32_t id)
{
    pthread_mutex_lock(&g_lock);
    Emitter *e = find_emitter(id);
    if (e) memset(e, 0, sizeof *e);
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_EmitterSetPosition(int32_t id, double x, double y, double z) { (void)id; (void)x; (void)y; (void)z; return E_NONE; }
API yyal_err YYAL_EmitterSetFalloff(int32_t id, double a, double b, double c) { (void)id; (void)a; (void)b; (void)c; return E_NONE; }

// A rampa (ms) e ignorada para emissores: o gain muda na hora.
API yyal_err YYAL_EmitterSetGain(int32_t id, double gain, int32_t ms)
{
    (void)ms;
    pthread_mutex_lock(&g_lock);
    Emitter *e = find_emitter(id);
    if (e) e->gain = (gain >= 0 && gain == gain) ? (float)gain : 0.0f;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_EmitterSetPitch(int32_t id, double pitch)
{
    pthread_mutex_lock(&g_lock);
    Emitter *e = find_emitter(id);
    if (e) e->pitch = (pitch > 0 && pitch == pitch) ? (float)pitch : 1.0f;
    pthread_mutex_unlock(&g_lock);
    return E_NONE;
}

API yyal_err YYAL_EmitterSetBus(int32_t id, int32_t bus) { (void)id; (void)bus; return E_NONE; }

// ---------------------------------------------------------------------------
// Buses e efeitos: no-op (handles validos, parametros devolvem 0)
API yyal_err YYAL_BusCreate(int32_t *out)
{
    if (!out) return E_PTR;
    pthread_mutex_lock(&g_lock); *out = new_handle(); pthread_mutex_unlock(&g_lock);
    return E_NONE;
}
API yyal_err YYAL_BusFree(int32_t id) { (void)id; return E_NONE; }
API yyal_err YYAL_BusGetParameter(int32_t id, int32_t param, double *v) { (void)id; (void)param; if (v) *v = 0.0; return E_NONE; }
API yyal_err YYAL_BusSetParameter(int32_t id, int32_t param, double v) { (void)id; (void)param; (void)v; return E_NONE; }
API yyal_err YYAL_BusGetEffect(int32_t id, int32_t slot, int32_t *out) { (void)id; (void)slot; if (out) *out = -1; return E_NONE; }
API yyal_err YYAL_BusSetEffect(int32_t id, int32_t slot, int32_t fx) { (void)id; (void)slot; (void)fx; return E_NONE; }
API yyal_err YYAL_BusClearEffect(int32_t id, int32_t slot) { (void)id; (void)slot; return E_NONE; }

API yyal_err YYAL_EffectCreate(int32_t type, double *params, int32_t *out)
{
    (void)type; (void)params;
    if (!out) return E_PTR;
    pthread_mutex_lock(&g_lock); *out = new_handle(); pthread_mutex_unlock(&g_lock);
    return E_NONE;
}
API yyal_err YYAL_EffectFree(int32_t id) { (void)id; return E_NONE; }
API yyal_err YYAL_EffectSetParam(int32_t id, int32_t param, double v) { (void)id; (void)param; (void)v; return E_NONE; }

// ---------------------------------------------------------------------------
// Fila de fim de reproducao. out aponta para um EndOfPlaybackEvent (12 bytes).
// None = evento escrito; Done = fila vazia (a IDE para o loop).
API yyal_err YYAL_PopEndOfPlaybackEvent(intptr_t out)
{
    if (!out) return E_PTR;
    pthread_mutex_lock(&g_lock);
    if (g_evq_count == 0) { pthread_mutex_unlock(&g_lock); return E_DONE; }
    EndEvent ev = g_evq[g_evq_head];
    g_evq_head = (g_evq_head + 1) % EVQ_SIZE;
    g_evq_count--;
    pthread_mutex_unlock(&g_lock);
    memcpy((void *)out, &ev, sizeof ev);
    return E_NONE;
}
