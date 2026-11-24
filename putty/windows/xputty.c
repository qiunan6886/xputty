/*
 * xputty.c: implementation of the xPuTTY extension hooks.
 *
 * This file is part of the xPuTTY fork of PuTTY.  It implements the
 * small amount of glue needed to embed a PuTTY terminal window inside
 * the xPuTTY shell, to feed macro text into the terminal, and to apply
 * the shell's colour theme.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdarg.h>

#include "putty.h"
#include "xputty.h"

/* ----------------------------------------------------------------------
 * Static state set up from the command line.
 */

static HWND embed_parent = NULL;
static HWND host_window = NULL;
static char *theme_file = NULL;

/* Debug tracing, enabled by setting XPUTTY_DEBUG=<path> in the
 * environment.  Only used while developing the shell. */
static void xputty_debug(const char *fmt, ...)
{
    static const char *path;
    static bool initialised;
    FILE *fp;
    va_list ap;
    char *msg;

    if (!initialised) {
        initialised = true;
        path = getenv("XPUTTY_DEBUG");
    }
    if (!path || !*path)
        return;
    va_start(ap, fmt);
    msg = dupvprintf(fmt, ap);
    va_end(ap);
    fp = fopen(path, "a");
    if (fp) {
        fprintf(fp, "%s\n", msg);
        fclose(fp);
    }
    sfree(msg);
}

UINT xputty_msg_created = 0;
UINT xputty_msg_exit = 0;
UINT xputty_msg_hotkey = 0;
UINT xputty_msg_fatal = 0;

void xputty_trace(const char *msg)
{
    xputty_debug("trace: %s", msg);
}

void xputty_trace_msg(unsigned int message, WPARAM wParam, LPARAM lParam)
{
    xputty_debug("wndproc msg=0x%04x wParam=0x%llx lParam=0x%llx",
                 message, (unsigned long long)wParam,
                 (unsigned long long)lParam);
}

void xputty_install_messages(void)
{
    if (!xputty_msg_created) {
        xputty_msg_created = RegisterWindowMessageW(L"xPuTTYCreated");
        xputty_msg_exit = RegisterWindowMessageW(L"xPuTTYSessionExit");
        xputty_msg_hotkey = RegisterWindowMessageW(L"xPuTTYHotkey");
        xputty_msg_fatal = RegisterWindowMessageW(L"xPuTTYFatal");
        xputty_debug("install_messages created=%u exit=%u hotkey=%u fatal=%u",
                     xputty_msg_created, xputty_msg_exit,
                     xputty_msg_hotkey, xputty_msg_fatal);
    }
}

bool xputty_embedded(void)
{
    return embed_parent != NULL;
}

HWND xputty_embed_parent(void)
{
    return embed_parent;
}

HWND xputty_host_window(void)
{
    return host_window;
}

/* ----------------------------------------------------------------------
 * Command line parsing.
 */

static HWND parse_hwnd(const char *s)
{
    char *end;
    unsigned long long v;

    if (!s || !*s)
        return NULL;
    v = strtoull(s, &end, 0);
    if (end == s)
        return NULL;
    return (HWND)(ULONG_PTR)v;
}

int xputty_cmdline_param(const char *arg, const char *nextarg,
                         int *skip_next)
{
    *skip_next = 0;
    if (!arg || strncmp(arg, "-xputty-", 8) != 0)
        return 0;

    if (!strcmp(arg, "-xputty-embed") || !strcmp(arg, "-xputty-host")) {
        HWND hwnd = parse_hwnd(nextarg);
        xputty_debug("cmdline %s nextarg=%s hwnd=%p", arg,
                     nextarg ? nextarg : "(null)", (void *)hwnd);
        if (hwnd) {
            if (!strcmp(arg, "-xputty-embed"))
                embed_parent = hwnd;
            else
                host_window = hwnd;
            *skip_next = 1;
        }
        return 1;
    }

    if (!strcmp(arg, "-xputty-theme")) {
        if (nextarg && *nextarg) {
            sfree(theme_file);
            theme_file = dupstr(nextarg);
            *skip_next = 1;
        }
        return 1;
    }

    /* Unknown xputty option: swallow it so that PuTTY does not treat it
     * as a fatal command-line error. */
    return 1;
}

/* ----------------------------------------------------------------------
 * Theme INI parsing.
 */

#define XPUTTY_NCOLOURS 22

struct ThemeData {
    char fontname[LF_FACESIZE];
    int fontsize;
    int colour[XPUTTY_NCOLOURS];
    int scrollback;
    int cursor;
    int bell;
    bool have_font;
};

static char *trim(char *s)
{
    char *end;
    while (*s && isspace((unsigned char)*s))
        s++;
    end = s + strlen(s);
    while (end > s && isspace((unsigned char)end[-1]))
        *--end = '\0';
    return s;
}

static int parse_hex_colour(const char *s, int *out)
{
    unsigned int v = 0;
    int i;

    if (*s == '#')
        s++;
    if (strlen(s) != 6)
        return 0;
    for (i = 0; i < 6; i++) {
        int c = (unsigned char)s[i];
        int d;
        if (c >= '0' && c <= '9')
            d = c - '0';
        else if (c >= 'a' && c <= 'f')
            d = c - 'a' + 10;
        else if (c >= 'A' && c <= 'F')
            d = c - 'A' + 10;
        else
            return 0;
        v = (v << 4) | d;
    }
    *out = (int)v;
    return 1;
}

static int colour_index(const char *key)
{
    static const struct {
        const char *name;
        int index;
    } names[] = {
        {"foreground", CONF_COLOUR_fg},
        {"fg", CONF_COLOUR_fg},
        {"foreground-bold", CONF_COLOUR_fg_bold},
        {"bold-foreground", CONF_COLOUR_fg_bold},
        {"background", CONF_COLOUR_bg},
        {"bg", CONF_COLOUR_bg},
        {"background-bold", CONF_COLOUR_bg_bold},
        {"bold-background", CONF_COLOUR_bg_bold},
        {"cursor-text", CONF_COLOUR_cursor_fg},
        {"cursor-foreground", CONF_COLOUR_cursor_fg},
        {"cursor", CONF_COLOUR_cursor_bg},
        {"cursor-colour", CONF_COLOUR_cursor_bg},
        {"cursor-background", CONF_COLOUR_cursor_bg},
        {"ansi-black", CONF_COLOUR_black},
        {"ansi-red", CONF_COLOUR_red},
        {"ansi-green", CONF_COLOUR_green},
        {"ansi-yellow", CONF_COLOUR_yellow},
        {"ansi-blue", CONF_COLOUR_blue},
        {"ansi-magenta", CONF_COLOUR_magenta},
        {"ansi-cyan", CONF_COLOUR_cyan},
        {"ansi-white", CONF_COLOUR_white},
        {"ansi-black-bold", CONF_COLOUR_black_bold},
        {"ansi-red-bold", CONF_COLOUR_red_bold},
        {"ansi-green-bold", CONF_COLOUR_green_bold},
        {"ansi-yellow-bold", CONF_COLOUR_yellow_bold},
        {"ansi-blue-bold", CONF_COLOUR_blue_bold},
        {"ansi-magenta-bold", CONF_COLOUR_magenta_bold},
        {"ansi-cyan-bold", CONF_COLOUR_cyan_bold},
        {"ansi-white-bold", CONF_COLOUR_white_bold},
    };

    int i;
    for (i = 0; i < (int)(sizeof(names)/sizeof(names[0])); i++)
        if (!strcmp(key, names[i].name))
            return names[i].index;

    /* ansi0 .. ansi15 */
    if (!strncmp(key, "ansi", 4) && isdigit((unsigned char)key[4])) {
        char *end;
        long n = strtol(key + 4, &end, 10);
        if (*end == '\0' && n >= 0 && n < 16) {
            if (n < 8)
                return CONF_COLOUR_black + 2 * (int)n;
            else
                return CONF_COLOUR_black_bold + 2 * (int)(n - 8);
        }
    }
    return -1;
}

static FILE *open_utf8_file(const char *path)
{
    wchar_t wpath[MAX_PATH * 2];
    int n = MultiByteToWideChar(CP_UTF8, 0, path, -1, wpath,
                                (int)(sizeof(wpath)/sizeof(wpath[0])));
    if (n <= 0)
        return NULL;
    return _wfopen(wpath, L"r");
}

void xputty_apply_theme(Conf *conf)
{
    struct ThemeData td;
    char line[1024];
    FILE *fp;
    FontSpec *oldfont;
    int i;

    xputty_trace("theme: start");
    memset(&td, 0, sizeof(td));
    td.fontsize = -1;
    td.scrollback = -1;
    td.cursor = -1;
    td.bell = -1;
    for (i = 0; i < XPUTTY_NCOLOURS; i++)
        td.colour[i] = -1;

    if (theme_file) {
        fp = open_utf8_file(theme_file);
        if (fp) {
            if (fgets(line, sizeof(line), fp)) {
                /* Strip a UTF-8 BOM if present. */
                if ((unsigned char)line[0] == 0xEF &&
                    (unsigned char)line[1] == 0xBB &&
                    (unsigned char)line[2] == 0xBF)
                    fseek(fp, 3, SEEK_SET);
                else
                    fseek(fp, 0, SEEK_SET);
            }
            while (fgets(line, sizeof(line), fp)) {
                char *key, *value, *eq;
                int idx;

                if ((unsigned char)line[0] == 0xEF &&
                    (unsigned char)line[1] == 0xBB &&
                    (unsigned char)line[2] == 0xBF)
                    memmove(line, line + 3, strlen(line + 3) + 1);

                key = trim(line);
                if (!*key || *key == '#' || *key == ';' || *key == '[')
                    continue;
                eq = strchr(key, '=');
                if (!eq)
                    continue;
                *eq = '\0';
                value = trim(eq + 1);
                key = trim(key);
                {
                    char *p;
                    for (p = key; *p; p++)
                        if (isupper((unsigned char)*p))
                            *p = (char)tolower((unsigned char)*p);
                }

                if (!strcmp(key, "font")) {
                    strncpy(td.fontname, value, sizeof(td.fontname) - 1);
                    td.fontname[sizeof(td.fontname) - 1] = '\0';
                    td.have_font = true;
                } else if (!strcmp(key, "font-size") ||
                           !strcmp(key, "fontsize")) {
                    td.fontsize = atoi(value);
                } else if (!strcmp(key, "scrollback")) {
                    td.scrollback = atoi(value);
                } else if (!strcmp(key, "cursor")) {
                    if (*value == '#' || isxdigit((unsigned char)*value)) {
                        int rgb;
                        if (parse_hex_colour(value, &rgb))
                            td.colour[CONF_COLOUR_cursor_bg] = rgb;
                    } else if (!strcmp(value, "block")) {
                        td.cursor = CURSOR_BLOCK;
                    } else if (!strcmp(value, "underline")) {
                        td.cursor = CURSOR_UNDERLINE;
                    } else if (!strcmp(value, "vline") ||
                               !strcmp(value, "vertical")) {
                        td.cursor = CURSOR_VERTICAL_LINE;
                    }
                } else if (!strcmp(key, "bell")) {
                    td.bell = (!strcmp(value, "off") ||
                               !strcmp(value, "none")) ? BELL_DISABLED
                                                       : BELL_DEFAULT;
                } else {
                    idx = colour_index(key);
                    if (idx >= 0) {
                        int rgb;
                        if (parse_hex_colour(value, &rgb))
                            td.colour[idx] = rgb;
                    }
                }
            }
            fclose(fp);
        }
    }

    xputty_trace("theme: parsed");
    oldfont = conf_get_fontspec(conf, CONF_font);
    if (oldfont && (td.have_font || td.fontsize > 0)) {
        FontSpec *fs = fontspec_new(td.have_font ? td.fontname
                                                 : oldfont->name,
                                    oldfont->isbold,
                                    td.fontsize > 0 ? td.fontsize
                                                    : oldfont->height,
                                    oldfont->charset);
        conf_set_fontspec(conf, CONF_font, fs);
        fontspec_free(fs);
    }

    xputty_trace("theme: font applied");
    for (i = 0; i < XPUTTY_NCOLOURS; i++) {
        if (td.colour[i] >= 0) {
            conf_set_int_int(conf, CONF_colours, i * 3 + 0,
                             (td.colour[i] >> 16) & 0xFF);
            conf_set_int_int(conf, CONF_colours, i * 3 + 1,
                             (td.colour[i] >> 8) & 0xFF);
            conf_set_int_int(conf, CONF_colours, i * 3 + 2,
                             td.colour[i] & 0xFF);
        }
    }
    xputty_trace("theme: colours applied");
    if (td.scrollback >= 0)
        conf_set_int(conf, CONF_savelines, td.scrollback);
    if (td.cursor >= 0)
        conf_set_int(conf, CONF_cursor_type, td.cursor);
    if (td.bell >= 0)
        conf_set_int(conf, CONF_beep, td.bell);

    xputty_trace("theme: terminal opts applied");
    if (xputty_embedded()) {
        xputty_trace("theme: embed block");
        /* Managed mode: the shell owns window management and error
         * reporting, so suppress the parts of PuTTY's UI that would
         * pop up outside the shell. */
        conf_set_bool(conf, CONF_warn_on_close, false);
        xputty_trace("theme: set warn_on_close");
        conf_set_int(conf, CONF_close_on_exit, FORCE_OFF);
        xputty_trace("theme: set close_on_exit");
        conf_set_bool(conf, CONF_alwaysontop, false);
        xputty_trace("theme: set alwaysontop");
        conf_set_bool(conf, CONF_sunken_edge, false);
        xputty_trace("theme: set sunken_edge");
        conf_set_int(conf, CONF_window_border, 0);
        xputty_trace("theme: set window_border");
    }
    xputty_trace("theme: done");
}

/* ----------------------------------------------------------------------
 * Window creation / positioning.
 */

void xputty_attach_window(HWND hwnd)
{
    LONG_PTR style;
    RECT r;
    int w = 800, h = 500;

    if (!xputty_embedded() || !hwnd)
        return;

    /*
     * Reparent the fully initialised terminal window into the shell's
     * pane and strip its decorations.  This is deliberately done after
     * PuTTY has finished creating the window, so that no window messages
     * arrive while WinGuiSeat is only half set up.
     */
    style = GetWindowLongPtr(hwnd, GWL_STYLE);
    style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX |
               WS_SYSMENU | WS_POPUP);
    style |= WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
    SetWindowLongPtr(hwnd, GWL_STYLE, style);

    SetParent(hwnd, embed_parent);

    if (GetClientRect(embed_parent, &r) && r.right > 0 && r.bottom > 0) {
        w = r.right;
        h = r.bottom;
    }
    SetWindowPos(hwnd, NULL, 0, 0, w, h,
                 SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOACTIVATE);
    xputty_debug("attach_window hwnd=%p parent=%p style=0x%llx size=%dx%d",
                 (void *)hwnd, (void *)embed_parent,
                 (unsigned long long)GetWindowLongPtr(hwnd, GWL_STYLE),
                 w, h);
}

void xputty_position_embedded(HWND hwnd)
{
    RECT r;
    int w = 800, h = 500;

    if (!xputty_embedded())
        return;
    if (GetClientRect(embed_parent, &r) && r.right > 0 && r.bottom > 0) {
        w = r.right;
        h = r.bottom;
    }
    SetWindowPos(hwnd, NULL, 0, 0, w, h,
                 SWP_NOZORDER | SWP_NOACTIVATE);
}

/* ----------------------------------------------------------------------
 * Notifications to the shell.
 */

void xputty_notify_created(HWND term_hwnd)
{
    xputty_debug("notify_created hwnd=%p host=%p msg=%u", (void *)term_hwnd,
                 (void *)host_window, xputty_msg_created);
    if (host_window && xputty_msg_created)
        PostMessageW(host_window, xputty_msg_created,
                     (WPARAM)(ULONG_PTR)term_hwnd, 0);
}

void xputty_notify_exit(int exitcode)
{
    if (host_window && xputty_msg_exit)
        PostMessageW(host_window, xputty_msg_exit,
                     (WPARAM)(INT_PTR)exitcode, 0);
}

bool xputty_notify_fatal(const char *message)
{
    /*
     * The shell wants fatal errors rendered inside the pane, not as a
     * system-modal MessageBox that could be hidden behind the shell.
     *
     * Use WM_COPYDATA rather than a pointer in a registered message: the
     * system marshals the WM_COPYDATA buffer across process boundaries,
     * whereas a raw pointer in lParam would be meaningless (or a security
     * hazard) in the shell's address space.
     */
    if (host_window && message) {
        COPYDATASTRUCT cds;
        cds.dwData = (ULONG_PTR)XPUTTY_COPYDATA_FATAL;
        cds.cbData = (DWORD)(strlen(message) + 1);
        cds.lpData = (void *)message;
        SendMessageW(host_window, WM_COPYDATA, 0, (LPARAM)&cds);
        return true;
    }
    return false;
}

static void notify_hotkey(WPARAM vk, int shift)
{
    if (host_window && xputty_msg_hotkey)
        PostMessageW(host_window, xputty_msg_hotkey,
                     vk, shift ? 1 : 0);
}

bool xputty_check_hotkey(WPARAM vk)
{
    bool ctrl, shift;

    if (!xputty_embedded() || !host_window)
        return false;

    ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
    shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;

    /* F11: maximise the current split pane (handled by the shell). */
    if (vk == VK_F11) {
        notify_hotkey(vk, shift);
        return true;
    }

    if (!ctrl)
        return false;

    switch (vk) {
      case 'T':
      case 'W':
      case 'E':
      case 'D':
      case 'M':
        if (shift) {
            notify_hotkey(vk, 1);
            return true;
        }
        break;
      case VK_TAB:
        notify_hotkey(vk, shift ? 1 : 0);
        return true;
      default:
        if (shift && vk >= '1' && vk <= '9') {
            notify_hotkey(vk, 1);
            return true;
        }
        break;
    }
    return false;
}

/* ----------------------------------------------------------------------
 * Macro text injection.
 */

void xputty_inject_text(Terminal *term, const void *payload, size_t len)
{
    const unsigned char *p = (const unsigned char *)payload;
    unsigned int flags;
    const char *utf8;
    size_t utf8len, i, outlen;
    wchar_t *wbuf;

    if (!term || !p || len < 4)
        return;

    flags = (unsigned int)p[0] | ((unsigned int)p[1] << 8) |
            ((unsigned int)p[2] << 16) | ((unsigned int)p[3] << 24);
    utf8 = (const char *)(p + 4);
    utf8len = len - 4;

    if (utf8len) {
        int wlen = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                       utf8, (int)utf8len, NULL, 0);
        if (wlen > 0) {
            wbuf = snewn(wlen + 1, wchar_t);
            MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                utf8, (int)utf8len, wbuf, wlen);
            /* Normalise line endings to CR, which is what the terminal
             * expects for "Enter". */
            outlen = 0;
            for (i = 0; i < (size_t)wlen; i++) {
                if (wbuf[i] == L'\r')
                    continue;          /* drop, CRLF -> CR below */
                if (wbuf[i] == L'\n')
                    wbuf[outlen++] = L'\r';
                else
                    wbuf[outlen++] = wbuf[i];
            }
            if (outlen)
                term_keyinputw(term, wbuf, (int)outlen);
            sfree(wbuf);
        }
    }

    if (flags & XPUTTY_INPUT_APPEND_CR)
        term_keyinputw(term, L"\r", 1);
}


