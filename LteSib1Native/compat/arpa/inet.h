#pragma once
/* Minimal POSIX networking decls — no Winsock (avoids wingdi ERROR=0 clash). */
#include <stdint.h>

#ifndef AF_INET
#define AF_INET 2
#endif
#ifndef SOCK_STREAM
#define SOCK_STREAM 1
#endif
#ifndef SOCK_DGRAM
#define SOCK_DGRAM 2
#endif

struct in_addr {
  uint32_t s_addr;
};

struct sockaddr {
  uint16_t sa_family;
  char     sa_data[14];
};

struct sockaddr_in {
  int16_t        sin_family;
  uint16_t       sin_port;
  struct in_addr sin_addr;
  char           sin_zero[8];
};

typedef int socklen_t;

#ifndef INET_ADDRSTRLEN
#define INET_ADDRSTRLEN 16
#endif
#ifndef INET6_ADDRSTRLEN
#define INET6_ADDRSTRLEN 46
#endif

#ifdef __cplusplus
extern "C" {
#endif

static inline uint32_t htonl(uint32_t hostlong)
{
#if defined(__BYTE_ORDER__) && (__BYTE_ORDER__ == __ORDER_BIG_ENDIAN__)
  return hostlong;
#else
  return __builtin_bswap32(hostlong);
#endif
}

static inline uint16_t htons(uint16_t hostshort)
{
#if defined(__BYTE_ORDER__) && (__BYTE_ORDER__ == __ORDER_BIG_ENDIAN__)
  return hostshort;
#else
  return (uint16_t)__builtin_bswap16(hostshort);
#endif
}

static inline uint32_t ntohl(uint32_t netlong)
{
  return htonl(netlong);
}

static inline uint16_t ntohs(uint16_t netshort)
{
  return htons(netshort);
}

#ifdef __cplusplus
}
#endif
