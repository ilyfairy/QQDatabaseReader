import type { ChatExportChat, ChatExportDocument, ChatExportMessage } from '@/types/chat-export'
import { localAssetUrl } from './safeUrl.ts'

export async function loadChatExport(): Promise<ChatExportDocument> {
  const existing = window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT__
  if (existing) {
    return existing
  }

  try {
    await loadChatExportScript()
    if (window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT__) {
      return window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT__
    }

    throw new Error('chat-data.js did not set export data')
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error)
    throw new Error(`没有读取到 resources/chat-data.js: ${detail}`)
  }
}

export async function loadChatExportFromFile(file: File): Promise<ChatExportDocument> {
  const text = await file.text()
  return parseImportedChatExport(text)
}

export function parseImportedChatExport(text: string): ChatExportDocument {
  const document = JSON.parse(text) as unknown
  if (!isRecord(document)) {
    throw new Error('聊天记录 JSON 的根节点必须是对象')
  }

  rejectMessageChunkManifest(document, '根节点')
  if (Array.isArray(document.chats)) {
    document.chats.forEach((chat, index) => {
      if (isRecord(chat)) {
        rejectMessageChunkManifest(chat, `chats[${index}]`)
      }
    })
  }

  return document as unknown as ChatExportDocument
}

function rejectMessageChunkManifest(value: Record<string, unknown>, location: string) {
  if (Object.prototype.hasOwnProperty.call(value, 'messageChunks')) {
    throw new Error(
      `手动导入的 JSON 不支持 ${location}.messageChunks；请选择包含内联 messages 的 chat.json`,
    )
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

const chunkPromises = new Map<string, Promise<ChatExportMessage[]>>()

export async function loadMessageChunk(source: ChatExportDocument | ChatExportChat, messageIndex: number) {
  if (source.messages) {
    return source.messages
  }

  const chunk = source.messageChunks?.find((item) =>
    messageIndex >= item.start && messageIndex < item.start + item.count,
  )
  if (!chunk) {
    return []
  }

  const chunkKey = chunk.key ?? String(chunk.index)
  let promise = chunkPromises.get(chunkKey)
  if (!promise) {
    promise = loadMessageChunkScript(chunkKey, chunk.path)
      .finally(() => chunkPromises.delete(chunkKey))
    chunkPromises.set(chunkKey, promise)
  }

  return promise
}

async function loadChatExportScript() {
  await new Promise<void>((resolve, reject) => {
    const script = document.createElement('script')
    script.src = './resources/chat-data.js'
    script.async = true
    script.onload = () => {
      script.remove()
      resolve()
    }
    script.onerror = () => {
      script.remove()
      reject(new Error('script load failed'))
    }
    document.head.append(script)
  })
}

async function loadMessageChunkScript(key: string, path: string) {
  const existing = window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT_MESSAGE_CHUNKS__?.[key]
  if (existing) {
    delete window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT_MESSAGE_CHUNKS__?.[key]
    return existing
  }

  const scriptUrl = localAssetUrl(path)
  if (!scriptUrl) {
    throw new Error(`message chunk path is not a local export resource: ${path}`)
  }

  await new Promise<void>((resolve, reject) => {
    const script = document.createElement('script')
    script.src = scriptUrl
    script.async = true
    script.onload = () => {
      script.remove()
      resolve()
    }
    script.onerror = () => {
      script.remove()
      reject(new Error(`message chunk load failed: ${path}`))
    }
    document.head.append(script)
  })

  const messages = window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT_MESSAGE_CHUNKS__?.[key] ?? []
  delete window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT_MESSAGE_CHUNKS__?.[key]
  return messages
}
