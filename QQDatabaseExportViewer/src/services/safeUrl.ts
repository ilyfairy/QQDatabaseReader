export function localAssetUrl(path?: string | null) {
  const value = path?.trim()
  if (!value || hasControlCharacters(value)) {
    return ''
  }

  const normalized = value.replace(/\\/g, '/')
  if (isAbsoluteOrSchemed(normalized)) {
    return ''
  }

  let decodedNormalized = normalized
  for (let depth = 0; depth < 8; depth += 1) {
    const encodedValue = decodedNormalized
    let decoded: string
    try {
      decoded = decodeURIComponent(encodedValue)
    } catch {
      return ''
    }

    decodedNormalized = decoded.replace(/\\/g, '/')
    if (
      hasControlCharacters(decodedNormalized) ||
      isAbsoluteOrSchemed(decodedNormalized) ||
      decodedNormalized.split('/').some((segment) => segment === '..')
    ) {
      return ''
    }

    if (decodedNormalized === encodedValue || !decodedNormalized.includes('%')) {
      break
    }

    if (depth === 7)
      return ''
  }

  const relativePath = normalized.replace(/^\.\/+/, '')
  return `./${relativePath.split('/').map((segment) => encodeURIComponent(segment)).join('/')}`
}

function isAbsoluteOrSchemed(value: string) {
  return value.startsWith('/') || /^[a-z][a-z0-9+.-]*:/i.test(value)
}

function hasControlCharacters(value: string) {
  return [...value].some((character) => {
    const code = character.charCodeAt(0)
    return code < 0x20 || code === 0x7f
  })
}

export function safeExternalUrl(value?: string | null) {
  const input = value?.trim()
  if (!input) {
    return ''
  }

  try {
    const url = new URL(input)
    return url.protocol === 'https:' || url.protocol === 'http:' ? url.href : ''
  } catch {
    return ''
  }
}
