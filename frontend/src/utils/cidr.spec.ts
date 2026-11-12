import { describe, expect, it } from 'vitest'
import { isIpAddress, normalizeCidr } from './cidr'
describe('normalizeCidr', () => {
  it.each([
    ['192.0.2.1', '192.0.2.1/32'],
    ['192.0.2.0/24', '192.0.2.0/24'],
    [' 10.0.0.0/8 ', '10.0.0.0/8'],
    ['2001:0DB8:0000:0000:0000:0000:0000:0001', '2001:db8::1/128'],
    ['2001:db8::/32', '2001:db8::/32'],
    ['::1', '::1/128'],
    ['::ffff:192.0.2.1', '192.0.2.1/32'],
    ['::ffff:c000:200/120', '192.0.2.0/24'],
    ['2001:db8:0:1:0:0:0:1', '2001:db8:0:1::1/128'],
    ['::192.0.2.1', '::192.0.2.1/128'],
    ['::ffff:0:192.0.2.1', '::ffff:0:192.0.2.1/128'],
    ['2001:db8::5efe:c000:201', '2001:db8::5efe:192.0.2.1/128'],
    ['64:ff9b::192.0.2.1', '64:ff9b::c000:201/128'],
  ])('Normalize_%s_ReturnsCanonicalCidr', (input, expected) => expect(normalizeCidr(input)).toEqual({ valid: true, normalized: expected }))
  it.each([
    '', '256.1.1.1', '10.1', '010.0.0.1', '10.0.0.0/+8', '10.0.0.0/', '10.0.0.0/33',
    '2001:db8::/129', '2001:::1', '1:2:3:4:5:6:7', '::1%eth0', '1::2::3', '::ffff:192.0.2.1/95',
    '0.0.0.0/0', '::/0', '::ffff:0.0.0.0/96', '10.0.0.0/8/1', '192.0.2.1/24', '2001:db8::1/64', '[::1]',
  ])('Normalize_%s_RejectsInvalidOrUnsafeInput', input => expect(normalizeCidr(input).valid).toBe(false))
  it('Normalize_HostBits_ReturnsSuggestedNetwork', () => expect(normalizeCidr('10.0.0.5/24').error).toContain('10.0.0.0/24'))
  it('IsIpAddress_CidrAndZone_RejectsNetworksAndZones', () => {
    expect(isIpAddress('192.0.2.1')).toBe(true)
    expect(isIpAddress('2001:db8::1')).toBe(true)
    expect(isIpAddress('192.0.2.0/24')).toBe(false)
    expect(isIpAddress('::1%lo')).toBe(false)
  })
})
