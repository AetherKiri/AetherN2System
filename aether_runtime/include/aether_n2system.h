#ifndef AETHER_N2SYSTEM_H
#define AETHER_N2SYSTEM_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* ABI v1. Calls for one instance must be serialized on its creating thread.
 * UTF-8 paths. Zero succeeds; -1 fails with last_error. No exceptions cross ABI.
 * Rendering is offscreen. No SDL window/event loop is created by this host. */
uint32_t aether_n2system_abi_version(void);
typedef struct aether_n2system_frame_info {
    const uint8_t *pixels; /* Borrowed RGBA8, top-to-bottom, valid until next tick/destroy. */
    uint32_t width, height, stride;
    uint64_t sequence;
} aether_n2system_frame_info;
int aether_n2system_create(const char *root, const char *writable_path, uint32_t width, uint32_t height, void **out_handle);
int aether_n2system_tick(void *handle, double delta_seconds);
/* 1/2 key down/up (USB/SDL scancode); 3 mouse move (x/y pixels);
 * 4/5 mouse down/up (code 1 left,2 middle,3 right); 6 wheel (x/y).
 * 7 text is reserved and returns an explicit unsupported error. */
int aether_n2system_event(void *handle, uint32_t type, int32_t code, float x, float y);
int aether_n2system_pause(void *handle, int paused);
int aether_n2system_frame(void *handle, aether_n2system_frame_info *out_frame);
int aether_n2system_status(void *handle); /* 0 running,1 paused,2 exited,-1 error */
const char *aether_n2system_last_error(void *handle); /* NULL handle: last create error */
void aether_n2system_destroy(void *handle);
#ifdef __cplusplus
}
#endif
#endif
