/*
 * xputty.h: xPuTTY extension hooks for the Windows PuTTY terminal.
 *
 * This is part of the xPuTTY fork of PuTTY (see the top-level README).
 * It adds a "managed terminal" mode used by the xPuTTY shell:
 *
 *   -xputty-embed <hwnd>   create the terminal window as a WS_CHILD of
 *                          the given host window, with no decorations
 *   -xputty-host <hwnd>    window that receives xputty notifications
 *   -xputty-theme <file>   apply a font/colour theme from an INI file
 *
 * Notifications are sent with registered window messages (see
 * xputty_install_messages), and terminal input can be injected from the
 * shell with a WM_COPYDATA message carrying the payload documented in
 * xputty.c.
 */

#ifndef PUTTY_WINDOWS_XPUTTY_H
#define PUTTY_WINDOWS_XPUTTY_H

#include "putty.h"

/* Magic values accepted in the dwData field of a WM_COPYDATA message. */
#define XPUTTY_COPYDATA_MAGIC 0x58505459u /* 'XPTY' - shell -> terminal input */
#define XPUTTY_COPYDATA_FATAL 0x58505446u /* 'XPTF' - terminal -> shell error */
#define XPUTTY_COPYDATA_THEME 0x58505454u /* 'XPTT' - shell -> live theme change */

/* Flags in the 4-byte little-endian header of an injected payload. */
#define XPUTTY_INPUT_APPEND_CR 0x00000001u

extern UINT xputty_msg_created;  /* (WPARAM)terminal hwnd */
extern UINT xputty_msg_exit;     /* (WPARAM)exit code, (LPARAM)0 */
extern UINT xputty_msg_hotkey;   /* (WPARAM)virtual key, (LPARAM)(shift?1:0) */
extern UINT xputty_msg_fatal;    /* synchronous; LPARAM points at UTF-8 text */

void xputty_install_messages(void);

/* Debug tracing used while developing the shell (no-op unless the
 * XPUTTY_DEBUG environment variable names a log file). */
void xputty_trace(const char *msg);
void xputty_trace_msg(unsigned int message, WPARAM wParam, LPARAM lParam);

/*
 * Command-line handling.  Returns 1 if the argument was an xputty
 * option (in which case *skip_next is set if a value argument was
 * consumed), 0 if it wasn't.
 */
int xputty_cmdline_param(const char *arg, const char *nextarg,
                         int *skip_next);
void xputty_apply_theme(Conf *conf);
/* Apply a theme INI to an already-running session's Conf (colours, font). */
void xputty_load_theme(Conf *conf, const char *path);

bool xputty_embedded(void);
HWND xputty_embed_parent(void);
HWND xputty_host_window(void);

/*
 * Window management: in embedded mode, turn the (already created and
 * initialised) top-level terminal window into a borderless child of the
 * shell's pane.
 */
void xputty_attach_window(HWND hwnd);

/* Move the embedded terminal to fill its embed parent. */
void xputty_position_embedded(HWND hwnd);

/* Notify the shell that the terminal window exists. */
void xputty_notify_created(HWND term_hwnd);

/* Notify the shell that the session/process is finishing. */
void xputty_notify_exit(int exitcode);

/* Route a fatal error to the shell instead of a MessageBox.  Returns
 * true if the shell was notified (so cleanup should not show a box). */
bool xputty_notify_fatal(const char *message);

/*
 * Hot-key forwarding.  Returns true if the key should be swallowed and
 * forwarded to the shell.
 */
bool xputty_check_hotkey(WPARAM vk);

/* Inject UTF-8 text (payload: 4-byte flags header, then UTF-8) into a
 * terminal, as if it had been typed. */
void xputty_inject_text(Terminal *term, const void *payload, size_t len);

#endif /* PUTTY_WINDOWS_XPUTTY_H */
