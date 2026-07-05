import type { ChatExportChat, ChatExportDocument, ChatExportMessage } from '@/types/chat-export'

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
  return JSON.parse(text) as ChatExportDocument
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
    chunkPromises.set(chunkKey, promise)
    promise.finally(() => chunkPromises.delete(chunkKey))
  }

  return promise
}

async function loadChatExportScript() {
  await new Promise<void>((resolve, reject) => {
    const script = document.createElement('script')
    script.src = './resources/chat-data.js'
    script.async = true
    script.onload = () => resolve()
    script.onerror = () => reject(new Error('script load failed'))
    document.head.append(script)
  })
}

async function loadMessageChunkScript(key: string, path: string) {
  const existing = window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT_MESSAGE_CHUNKS__?.[key]
  if (existing) {
    delete window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT_MESSAGE_CHUNKS__?.[key]
    return existing
  }

  await new Promise<void>((resolve, reject) => {
    const script = document.createElement('script')
    script.src = `./${path}`
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
