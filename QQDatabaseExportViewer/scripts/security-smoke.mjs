import { parseImportedChatExport } from '../src/services/chatExportLoader.ts'
import { localAssetUrl, safeExternalUrl } from '../src/services/safeUrl.ts'

const inlineDocument = parseImportedChatExport(JSON.stringify({
  schemaVersion: '1.0',
  messages: [],
  chats: [{ key: 'chat-1', messages: [] }],
}))
requireCondition(Array.isArray(inlineDocument.messages), 'inline messages JSON was rejected')

expectRejected({
  messages: null,
  messageChunks: [{ path: '../payload.js' }],
}, 'top-level traversal chunk manifest was accepted')

expectRejected({
  chats: [{
    key: 'chat-1',
    messages: null,
    messageChunks: [{ path: '../../same-origin.js' }],
  }],
}, 'nested same-origin chunk manifest was accepted')

expectRejected({
  messages: [],
  messageChunks: [],
}, 'empty chunk manifest field was accepted')

requireCondition(
  localAssetUrl('resources/messages/message-00000.js') === './resources/messages/message-00000.js',
  'valid local export resource was rejected',
)
requireCondition(
  localAssetUrl('resources/files/a#b%20.txt') === './resources/files/a%23b%2520.txt',
  'literal URL metacharacters in an exported file name were not encoded',
)
for (const path of [
  '../payload.js',
  '..\\payload.js',
  '%2e%2e/payload.js',
  '%252e%252e%252fpayload.js',
  '//example.test/payload.js',
  '\\\\example.test\\payload.js',
  'https://example.test/payload.js',
  'file:///C:/payload.js',
  'data:text/javascript,alert(1)',
  'javascript:alert(1)',
]) {
  requireCondition(localAssetUrl(path) === '', `unsafe local asset path was accepted: ${path}`)
}

requireCondition(
  safeExternalUrl('https://example.test/path') === 'https://example.test/path',
  'HTTPS external link was rejected',
)
requireCondition(
  safeExternalUrl('http://example.test/path') === 'http://example.test/path',
  'HTTP external link was rejected',
)
for (const url of ['file:///C:/payload', 'data:text/html,payload', 'javascript:alert(1)', '//example.test/path']) {
  requireCondition(safeExternalUrl(url) === '', `unsafe external URL was accepted: ${url}`)
}

console.log('viewer import security smoke passed')

function expectRejected(value, message) {
  try {
    parseImportedChatExport(JSON.stringify(value))
  } catch {
    return
  }

  throw new Error(message)
}

function requireCondition(condition, message) {
  if (!condition) {
    throw new Error(message)
  }
}
