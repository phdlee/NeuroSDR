#pragma once
#include <string.h>
#ifndef bzero
#define bzero(p, n) memset((p), 0, (size_t)(n))
#endif
#ifndef bcopy
#define bcopy(src, dst, n) memmove((dst), (src), (size_t)(n))
#endif
#ifndef strcasecmp
#define strcasecmp _stricmp
#endif
#ifndef strncasecmp
#define strncasecmp _strnicmp
#endif
