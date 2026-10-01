#pragma once
/* Minimal pwd.h for MinGW — dft_fftw only needs getpwuid/getuid decls. */
#include <sys/types.h>

#ifndef uid_t
typedef unsigned uid_t;
#endif
#ifndef gid_t
typedef unsigned gid_t;
#endif

struct passwd {
  char* pw_name;
  char* pw_dir;
  uid_t pw_uid;
  gid_t pw_gid;
};

static inline uid_t getuid(void)
{
  return 0;
}

static inline struct passwd* getpwuid(uid_t uid)
{
  (void)uid;
  static struct passwd pw;
  static char          dir[] = ".";
  pw.pw_name                 = dir;
  pw.pw_dir                  = dir;
  pw.pw_uid                  = 0;
  pw.pw_gid                  = 0;
  return &pw;
}
