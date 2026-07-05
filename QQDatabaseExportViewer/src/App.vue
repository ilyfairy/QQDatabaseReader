<script setup lang="ts">
import { useVirtualizer, type VirtualItem } from '@tanstack/vue-virtual'
import { layout, measureNaturalWidth, prepareWithSegments } from '@chenglou/pretext'
import { computed, nextTick, onMounted, onUnmounted, ref, shallowRef, watch } from 'vue'
import MessageItem from '@/components/MessageItem.vue'
import { loadChatExport, loadChatExportFromFile, loadMessageChunk } from '@/services/chatExportLoader'
import type {
  ChatExportChat,
  ChatExportConversation,
  ChatExportDocument,
  ChatExportMedia,
  ChatExportMessage,
  ChatExportMessageIndex,
  ChatExportReply,
  ChatExportSegment,
} from '@/types/chat-export'

type TimelineRowMeta =
  | {
      kind: 'date'
      key: string
      label: string
      size: number
    }
  | {
      kind: 'message'
      key: string
      messageIndex: number
    }

type TimelineRow =
  | {
      kind: 'date'
      key: string
      label: string
      size: number
    }
  | {
      kind: 'message'
      key: string
      message: ChatExportMessage
      messageIndex: number
      size: number
    }

interface SearchHit {
  key: string
  index: number
  preview: string
}

interface RenderedTimelineRow {
  virtualRow: VirtualItem
  row: TimelineRow
}

interface FullTimelineRow {
  rowIndex: number
  row: TimelineRow
}

const searchDebounceMilliseconds = 500
const keyScrollStepRatio = 0.9
const maxBubbleOuterWidth = 720
const bubbleHorizontalChrome = 22
const bubbleVerticalChrome = 18
const rowVerticalPadding = 10
const messageHeadHeight = 20
const avatarHeight = 40
const messageColumnGap = 10
const segmentGap = 4
const textLineHeight = 25
const replyMarginBottom = 6
const textFont = '16px Inter, "Microsoft YaHei UI", "Segoe UI", Arial, "Apple Color Emoji", "Segoe UI Emoji", sans-serif'
const replyTextFont = '13px Inter, "Microsoft YaHei UI", "Segoe UI", Arial, "Apple Color Emoji", "Segoe UI Emoji", sans-serif'
const replyTextLineHeight = 18
const replySenderLineHeight = 16
const defaultMessageRowSize = 96
const preparedTextCacheLimit = 512
const loadedMessageChunkLimit = 8
const visibleMessageLoadDelayMilliseconds = 150
const defaultTimelineContentWidth = maxBubbleOuterWidth + avatarHeight + messageColumnGap
const defaultConversationListWidth = 280
const minConversationListWidth = 180
const maxConversationListWidth = 520
const collapseConversationListThreshold = 128
const collapsedConversationListWidth = 22

type TextWhiteSpace = 'normal' | 'pre-wrap'

interface TextMeasureStyle {
  font: string
  whiteSpace: TextWhiteSpace
}

const messageTextStyle: TextMeasureStyle = {
  font: textFont,
  whiteSpace: 'pre-wrap',
}

const replyTextStyle: TextMeasureStyle = {
  font: replyTextFont,
  whiteSpace: 'normal',
}

const documentData = ref<ChatExportDocument | null>(null)
const errorMessage = ref('')
const fileInput = ref<HTMLInputElement | null>(null)
const timelineRef = ref<HTMLElement | null>(null)
const timelineScrollRef = ref<HTMLDivElement | null>(null)
const conversationListRef = ref<HTMLElement | null>(null)
const searchInputRef = ref<HTMLInputElement | null>(null)
const isSearchOpen = ref(false)
const isSettingsOpen = ref(false)
const isVirtualizationEnabled = ref(true)
const isLoadingAllMessages = ref(false)
const conversationListWidth = ref(defaultConversationListWidth)
const isConversationListCollapsed = ref(false)
const isConversationListResizing = ref(false)
const searchText = ref('')
const searchHits = shallowRef<SearchHit[]>([])
const activeSearchIndex = ref(-1)
const searchStatus = ref('')
const isSearching = ref(false)
const transientHighlightKey = ref('')
const timelineContentWidth = ref(defaultTimelineContentWidth)
const activeChatKey = ref('')

let searchTimer = 0
let visibleLoadTimer = 0
let transientHighlightTimer = 0
let searchRequestId = 0
let activeChatLoadVersion = 0
let timelineResizeObserver: ResizeObserver | null = null
let conversationListResizeLeft = 0
let conversationListWidthBeforeResize = defaultConversationListWidth
let lastExpandedConversationListWidth = defaultConversationListWidth
let documentResizeCursor = ''
let documentResizeUserSelect = ''
const messageCache = shallowRef(new Map<number, ChatExportMessage>())
const rowSizeCache = shallowRef(new Map<number, number>())
const loadingChunks = new Map<string, Promise<void>>()
const loadedChunkRanges: Array<{ key: string; index: number; start: number; count: number }> = []
const searchChunkCache = new Map<string, Promise<ChatExportMessage[]>>()
const replyIndexBySeq = new Map<string, ChatExportMessageIndex>()
const replyIndexByMessageId = new Map<string, ChatExportMessageIndex>()
const replyIndexByRandom = new Map<string, ChatExportMessageIndex>()

const rows = computed<TimelineRowMeta[]>(() => {
  const result: TimelineRowMeta[] = []
  const chat = activeChat.value
  const source = chat?.messages ?? []
  const count = chat?.messageCount ?? source.length
  const timelineDates = chat?.timelineDates

  if (timelineDates?.length) {
    let dateIndex = 0
    for (let index = 0; index < count; index++) {
      while (timelineDates[dateIndex]?.messageIndex === index) {
        const date = timelineDates[dateIndex]
        if (!date) {
          break
        }
        result.push({
          kind: 'date',
          key: `date:${date.label}:${index}`,
          label: date.label,
          size: 34,
        })
        dateIndex++
      }

      const message = messageAt(index)
      result.push({
        kind: 'message',
        key: message?.key ?? `message:${index}`,
        messageIndex: index,
      })
    }

    return result
  }

  let previousDate = ''

  for (let index = 0; index < count; index++) {
    const message = source[index]
    if (!message) {
      continue
    }

    const date = message.localTime.slice(0, 10) || '未知日期'
    if (date !== previousDate) {
      result.push({
        kind: 'date',
        key: `date:${date}:${index}`,
        label: date,
        size: 34,
      })
      previousDate = date
    }

    result.push({
      kind: 'message',
      key: message.key,
      messageIndex: index,
    })
  }

  return result
})

const hasSearch = computed(() => searchText.value.trim().length > 0)
const currentHit = computed(() =>
  activeSearchIndex.value >= 0 ? searchHits.value[activeSearchIndex.value] : undefined,
)
const exportedChats = computed<ChatExportChat[]>(() => {
  const data = documentData.value
  if (!data) {
    return []
  }

  if (data.chats?.length) {
    return data.chats
  }

  return [createLegacyChat(data)]
})
const activeChat = computed(() =>
  exportedChats.value.find((chat) => chat.key === activeChatKey.value) ?? exportedChats.value[0] ?? null,
)
const activeConversation = computed(() => activeChat.value?.conversation ?? documentData.value?.conversation ?? null)
const activeMessageCount = computed(() => activeChat.value?.messageCount ?? 0)
const isMergedExport = computed(() => exportedChats.value.length > 1 || documentData.value?.conversation.logicalType === 'merged')
const viewerStyle = computed<Record<string, string>>(() => {
  if (!isMergedExport.value) {
    return {} as Record<string, string>
  }

  const width = isConversationListCollapsed.value ? collapsedConversationListWidth : conversationListWidth.value
  return {
    '--conversation-list-width': `${width}px`,
  }
})

function createLegacyChat(data: ChatExportDocument): ChatExportChat {
  return {
    key: data.conversation.key,
    conversation: data.conversation,
    participants: data.participants,
    messageCount: data.metadata.messageCount ?? data.messages?.length ?? 0,
    messages: data.messages,
    messageChunks: data.messageChunks,
    timelineDates: data.timelineDates,
    messageIndex: data.messageIndex,
  }
}

function chunkCacheKey(chunk: { key?: string; index: number }) {
  return chunk.key ?? String(chunk.index)
}
const rowVirtualizer = useVirtualizer<HTMLDivElement, HTMLDivElement>(
  computed(() => ({
    count: rows.value.length,
    getScrollElement: () => timelineScrollRef.value,
    estimateSize: rowSize,
    getItemKey: (index) => rows.value[index]?.key ?? index,
    overscan: 12,
  })),
)
const virtualRows = computed(() => rowVirtualizer.value.getVirtualItems())
const timelineHeight = computed(() => rowVirtualizer.value.getTotalSize())
const renderedRows = computed<RenderedTimelineRow[]>(() =>
  virtualRows.value.flatMap((virtualRow) => {
    const row = materializeRow(virtualRow.index)
    return row ? [{ virtualRow, row }] : []
  }),
)
const fullRows = computed<FullTimelineRow[]>(() => {
  if (isVirtualizationEnabled.value) {
    return []
  }

  return rows.value.flatMap((_, rowIndex) => {
    const row = materializeRow(rowIndex)
    return row ? [{ rowIndex, row }] : []
  })
})

onMounted(async () => {
  document.addEventListener('keydown', handleGlobalKeyDown, true)

  try {
    documentData.value = await loadChatExport()
    await afterDocumentLoaded()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  }
})

onUnmounted(() => {
  document.removeEventListener('keydown', handleGlobalKeyDown, true)
  window.clearTimeout(searchTimer)
  window.clearTimeout(visibleLoadTimer)
  window.clearTimeout(transientHighlightTimer)
  finishConversationListResize()
  timelineResizeObserver?.disconnect()
})

watch(searchText, () => {
  scheduleSearch()
})

async function afterDocumentLoaded() {
  activeChatKey.value = exportedChats.value[0]?.key ?? ''
  await activateChat(activeChatKey.value, false)
}

async function activateChat(chatKey: string, scrollToTop = true) {
  const chat = exportedChats.value.find((item) => item.key === chatKey) ?? exportedChats.value[0]
  if (!chat) {
    return
  }

  activeChatKey.value = chat.key
  activeChatLoadVersion += 1
  searchRequestId += 1
  isSettingsOpen.value = false
  isLoadingAllMessages.value = false
  searchText.value = ''
  searchHits.value = []
  activeSearchIndex.value = -1
  searchStatus.value = ''
  isSearching.value = false
  transientHighlightKey.value = ''
  resetLoadedMessages()
  await nextTick()
  observeTimelineResize()
  if (scrollToTop) {
    rowVirtualizer.value.scrollToOffset(0, { behavior: 'auto' })
  } else {
    rowVirtualizer.value.scrollToOffset(0, { behavior: 'auto' })
  }
  if (isVirtualizationEnabled.value) {
    await ensureVisibleMessagesLoaded()
  } else {
    await loadFullTimelineForActiveChat(true)
  }
}

function resetLoadedMessages() {
  messageCache.value = new Map()
  rowSizeCache.value = new Map()
  loadingChunks.clear()
  loadedChunkRanges.splice(0)
  searchChunkCache.clear()
  preparedTextCache.clear()
  rebuildReplyIndex()
}

function rebuildReplyIndex() {
  replyIndexBySeq.clear()
  replyIndexByMessageId.clear()
  replyIndexByRandom.clear()

  const chat = activeChat.value
  if (chat?.messageIndex) {
    for (const message of chat.messageIndex) {
      addReplyIndex(message)
    }
    return
  }

  chat?.messages?.forEach((message, index) => addReplyIndex({
    index,
    key: message.key,
    conversationKey: message.raw.conversationKey,
    messageId: message.messageId,
    messageRandom: message.messageRandom,
    messageSeq: message.messageSeq,
  }))
}

function addReplyIndex(message: ChatExportMessageIndex) {
  if (message.messageSeq !== 0) {
    addReplyIndexValue(replyIndexBySeq, message.conversationKey, message.messageSeq, message)
  }
  if (message.messageId !== 0) {
    addReplyIndexValue(replyIndexByMessageId, message.conversationKey, message.messageId, message)
  }
  if (message.messageRandom !== 0) {
    addReplyIndexValue(replyIndexByRandom, message.conversationKey, message.messageRandom, message)
  }
}

function addReplyIndexValue(
  map: Map<string, ChatExportMessageIndex>,
  conversationKey: string,
  value: number,
  message: ChatExportMessageIndex,
) {
  const preciseKey = replyLookupKey(conversationKey, value)
  if (!map.has(preciseKey)) {
    map.set(preciseKey, message)
  }

  const fallbackKey = replyLookupKey('', value)
  if (!map.has(fallbackKey)) {
    map.set(fallbackKey, message)
  }
}

function replyLookupKey(conversationKey: string | undefined, value: number) {
  return `${conversationKey || '*'}:${value}`
}

watch(virtualRows, () => {
  if (isVirtualizationEnabled.value) {
    scheduleVisibleMessagesLoad()
  }
})

function rowSize(index: number) {
  const row = rows.value[index]
  if (!row) {
    return defaultMessageRowSize
  }

  return row.kind === 'date'
    ? row.size
    : rowSizeCache.value.get(index) ?? defaultMessageRowSize
}

function materializeRow(index: number): TimelineRow | null {
  const row = rows.value[index]
  if (!row) {
    return null
  }

  if (row.kind === 'date') {
    return row
  }

  const message = messageAt(row.messageIndex)
  if (!message) {
    scheduleVisibleMessagesLoad()
    return null
  }

  ensureMessageRowSize(row.messageIndex)

  return {
    kind: 'message',
    key: message.key,
    message,
    messageIndex: row.messageIndex,
    size: rowSize(index),
  }
}

function messageAt(messageIndex: number) {
  return activeChat.value?.messages?.[messageIndex] ?? messageCache.value.get(messageIndex)
}

async function ensureVisibleMessagesLoaded() {
  if (!isVirtualizationEnabled.value) {
    return
  }

  const pending = virtualRows.value
    .map((row) => rows.value[row.index])
    .filter((row): row is Extract<TimelineRowMeta, { kind: 'message' }> =>
      row?.kind === 'message' &&
      (!messageAt(row.messageIndex) || !rowSizeCache.value.has(messageIndexToRowIndex(row.messageIndex))),
    )

  await Promise.all(pending.map(async (row) => {
    await ensureMessageLoaded(row.messageIndex)
    ensureMessageRowSize(row.messageIndex)
  }))
}

function scheduleVisibleMessagesLoad() {
  if (!isVirtualizationEnabled.value) {
    return
  }

  window.clearTimeout(visibleLoadTimer)
  visibleLoadTimer = window.setTimeout(() => {
    void ensureVisibleMessagesLoaded()
  }, visibleMessageLoadDelayMilliseconds)
}

async function toggleVirtualization() {
  if (isVirtualizationEnabled.value) {
    await disableVirtualization()
    return
  }

  enableVirtualization()
}

async function disableVirtualization() {
  isVirtualizationEnabled.value = false
  await loadFullTimelineForActiveChat(true)
}

async function loadFullTimelineForActiveChat(scrollToTop: boolean) {
  isLoadingAllMessages.value = true
  window.clearTimeout(visibleLoadTimer)
  try {
    await loadAllMessages()
    for (let index = 0; index < rows.value.length; index++) {
      const row = rows.value[index]
      if (row?.kind === 'message') {
        ensureMessageRowSize(row.messageIndex)
      }
    }
    await nextTick()
    if (scrollToTop) {
      timelineScrollElement()?.scrollTo({ top: 0, behavior: 'auto' })
    }
  } finally {
    isLoadingAllMessages.value = false
  }
}

function enableVirtualization() {
  isVirtualizationEnabled.value = true
  void nextTick().then(() => {
    recomputeCachedRowSizes()
    rowVirtualizer.value.scrollToOffset(0, { behavior: 'auto' })
    scheduleVisibleMessagesLoad()
  })
}

async function loadAllMessages() {
  const chat = activeChat.value
  const total = chat?.messageCount ?? chat?.messages?.length ?? 0
  if (!chat || total <= 0) {
    return
  }

  if (chat.messages) {
    return
  }

  const loadVersion = activeChatLoadVersion
  for (let index = 0; index < total;) {
    if (loadVersion !== activeChatLoadVersion) {
      return
    }

    const messageIndex = index
    await ensureMessageLoaded(messageIndex)
    if (loadVersion !== activeChatLoadVersion) {
      return
    }

    const chunk = chat.messageChunks?.find((item) =>
      messageIndex >= item.start && messageIndex < item.start + item.count,
    )
    index += chunk ? Math.max(1, chunk.count - Math.max(0, messageIndex - chunk.start)) : 1
    await nextFrame()
  }
}

async function ensureMessageLoaded(messageIndex: number) {
  const chat = activeChat.value
  if (!chat || chat.messages?.[messageIndex] || messageCache.value.has(messageIndex)) {
    return
  }

  const chunk = chat.messageChunks?.find((item) =>
    messageIndex >= item.start && messageIndex < item.start + item.count,
  )
  if (!chunk) {
    return
  }

  const cacheKey = chunkCacheKey(chunk)
  let loadTask = loadingChunks.get(cacheKey)
  if (!loadTask) {
    loadTask = loadMessageChunkIntoCache(chat, chunk, activeChatLoadVersion)
    loadingChunks.set(cacheKey, loadTask)
    loadTask.finally(() => {
      if (loadingChunks.get(cacheKey) === loadTask) {
        loadingChunks.delete(cacheKey)
      }
    })
  }

  await loadTask
}

async function loadMessagesForLookup(messageIndex: number) {
  const chat = activeChat.value
  if (!chat) {
    return []
  }

  if (chat.messages) {
    return chat.messages
  }

  const chunk = chat.messageChunks?.find((item) =>
    messageIndex >= item.start && messageIndex < item.start + item.count,
  )
  if (!chunk) {
    return []
  }

  const cacheKey = chunkCacheKey(chunk)
  let task = searchChunkCache.get(cacheKey)
  if (!task) {
    task = loadMessageChunk(chat, chunk.start)
    searchChunkCache.set(cacheKey, task)
    task.finally(() => {
      if (searchChunkCache.get(cacheKey) === task) {
        searchChunkCache.delete(cacheKey)
      }
    })
  }

  return task
}

async function loadMessageChunkIntoCache(
  chat: ChatExportChat,
  chunk: { key?: string; index: number; start: number; count: number },
  loadVersion: number,
) {
  const messages = await loadMessageChunk(chat, chunk.start)
  if (loadVersion !== activeChatLoadVersion) {
    return
  }

  const nextMessages = new Map(messageCache.value)
  for (let offset = 0; offset < messages.length; offset++) {
    const message = messages[offset]
    if (!message) {
      continue
    }
    const absoluteIndex = chunk.start + offset
    nextMessages.set(absoluteIndex, message)
  }
  rememberLoadedChunk(nextMessages, chunk)
  messageCache.value = nextMessages
}

function rememberLoadedChunk(
  cache: Map<number, ChatExportMessage>,
  chunk: { key?: string; index: number; start: number; count: number },
) {
  const cacheKey = chunkCacheKey(chunk)
  const existingIndex = loadedChunkRanges.findIndex((item) => item.key === cacheKey)
  if (existingIndex >= 0) {
    loadedChunkRanges.splice(existingIndex, 1)
  }

  loadedChunkRanges.push({ ...chunk, key: cacheKey })
  if (!isVirtualizationEnabled.value) {
    return
  }

  while (loadedChunkRanges.length > loadedMessageChunkLimit) {
    const removed = loadedChunkRanges.shift()
    if (!removed) {
      break
    }

    for (let index = removed.start; index < removed.start + removed.count; index++) {
      cache.delete(index)
    }
  }
}

function messageIndexToRowIndex(messageIndex: number) {
  const timelineDates = activeChat.value?.timelineDates
  if (timelineDates?.length) {
    return messageIndex + countTimelineDatesBeforeOrAt(messageIndex, timelineDates)
  }

  return rows.value.findIndex(row => row.kind === 'message' && row.messageIndex === messageIndex)
}

function countTimelineDatesBeforeOrAt(messageIndex: number, timelineDates: ChatExportDocument['timelineDates']) {
  let low = 0
  let high = timelineDates?.length ?? 0

  while (low < high) {
    const middle = Math.floor((low + high) / 2)
    if ((timelineDates?.[middle]?.messageIndex ?? Number.MAX_SAFE_INTEGER) <= messageIndex) {
      low = middle + 1
    } else {
      high = middle
    }
  }

  return low
}

function ensureMessageRowSize(messageIndex: number) {
  const message = messageAt(messageIndex)
  if (!message) {
    return
  }

  const rowIndex = messageIndexToRowIndex(messageIndex)
  if (rowIndex < 0 || rowSizeCache.value.has(rowIndex)) {
    return
  }

  const nextSizes = new Map(rowSizeCache.value)
  const size = estimateMessageRowSize(message)
  nextSizes.set(rowIndex, size)
  rowSizeCache.value = nextSizes
  rowVirtualizer.value.resizeItem(rowIndex, size)
}

function estimateMessageRowSize(message: ChatExportMessage) {
  if (message.isSystemHint) {
    return estimateSystemHintRowSize(message)
  }

  const visualOnly = isVisualMediaOnlyMessage(message)
  const textWidth = currentBubbleContentWidth()
  const segmentsHeight = estimateSegmentsHeight(message.segments, textWidth)
  let bubbleHeight = visualOnly ? segmentsHeight : bubbleVerticalChrome + Math.max(textLineHeight, segmentsHeight)
  if (message.reply) {
    bubbleHeight += estimateReplyHeight(message.reply.previewText, textWidth)
  }
  if (message.forwardedMessages.length > 0) {
    bubbleHeight += 36
  }

  let mainHeight = messageHeadHeight + bubbleHeight
  if (message.reactions.length > 0) {
    mainHeight += 27
  }
  if (message.isRecalled) {
    mainHeight += 8
  }

  return Math.ceil(rowVerticalPadding + Math.max(avatarHeight, mainHeight))
}

function isVisualMediaOnlyMessage(message: ChatExportMessage) {
  if (message.reply || message.forwardedMessages.length > 0 || message.segments.length !== 1) {
    return false
  }

  const media = message.segments[0]?.media
  return !!media?.relativePath && (media.kind === 'Image' || media.kind === 'Video')
}

function estimateSystemHintRowSize(message: ChatExportMessage) {
  const text = [message.systemHint?.sourceName, message.systemHint?.action, message.systemHint?.targetName, message.systemHint?.suffix, message.displayText]
    .filter(Boolean)
    .join(' ')
  return Math.ceil(rowVerticalPadding + 10 + Math.max(18, estimateTextHeight(text, currentSystemHintContentWidth(), 18)))
}

function estimateReplyHeight(text: string, bubbleContentWidth: number) {
  return replyMarginBottom + 12 + replySenderLineHeight + 2 + estimateTextHeight(
    text,
    Math.max(1, bubbleContentWidth - 16),
    replyTextLineHeight,
    replyTextStyle,
  )
}

function estimateSegmentsHeight(segments: ChatExportSegment[], maxWidth: number) {
  if (segments.length > 0 && segments.every(isPretextFlowSegment)) {
    const text = segments.map(segmentDisplayText).join('')
    return estimateTextHeight(text, maxWidth, textLineHeight)
  }

  if (segments.some(isBlockFlowSegment)) {
    let totalHeight = 0
    let visibleCount = 0
    for (const segment of segments) {
      const box = estimateSegmentBox(segment, maxWidth)
      if (!box) {
        continue
      }

      totalHeight += box.height
      visibleCount += 1
    }

    return totalHeight + Math.max(0, visibleCount - 1) * segmentGap
  }

  let totalHeight = 0
  let rowWidth = 0
  let rowHeight = 0

  for (const segment of segments) {
    const box = estimateSegmentBox(segment, maxWidth)
    if (!box) {
      continue
    }

    const nextWidth = rowWidth <= 0 ? box.width : rowWidth + segmentGap + box.width
    if (rowWidth > 0 && nextWidth > maxWidth) {
      totalHeight += rowHeight + (totalHeight > 0 ? segmentGap : 0)
      rowWidth = box.width
      rowHeight = box.height
      continue
    }

    rowWidth = nextWidth
    rowHeight = Math.max(rowHeight, box.height)
  }

  return totalHeight + rowHeight
}

function currentBubbleContentWidth() {
  const messageColumnWidth = Math.max(1, timelineContentWidth.value - avatarHeight - messageColumnGap)
  return Math.max(1, Math.floor(Math.min(maxBubbleOuterWidth, messageColumnWidth) - bubbleHorizontalChrome))
}

function currentSystemHintContentWidth() {
  return Math.max(1, Math.floor(Math.min(maxBubbleOuterWidth, timelineContentWidth.value) - 20))
}

function observeTimelineResize() {
  timelineResizeObserver?.disconnect()
  timelineResizeObserver = null
  updateTimelineContentWidth(true)

  const element = timelineScrollRef.value
  if (!element || typeof ResizeObserver === 'undefined') {
    return
  }

  timelineResizeObserver = new ResizeObserver(() => updateTimelineContentWidth())
  timelineResizeObserver.observe(element)
}

function updateTimelineContentWidth(force = false) {
  const element = timelineScrollRef.value
  if (!element) {
    return
  }

  const style = window.getComputedStyle(element)
  const contentWidth = Math.max(
    1,
    Math.floor(element.clientWidth - cssPixels(style.paddingLeft) - cssPixels(style.paddingRight)),
  )

  if (!force && Math.abs(contentWidth - timelineContentWidth.value) < 1) {
    return
  }

  const anchorRowIndex = force ? -1 : currentViewportAnchorRowIndex()
  timelineContentWidth.value = contentWidth
  recomputeCachedRowSizes()
  if (isVirtualizationEnabled.value && anchorRowIndex >= 0) {
    window.requestAnimationFrame(() => {
      rowVirtualizer.value.scrollToIndex(anchorRowIndex, { align: 'center', behavior: 'auto' })
      scheduleVisibleMessagesLoad()
    })
  }
}

function recomputeCachedRowSizes() {
  const touchedRowIndexes = new Set(rowSizeCache.value.keys())
  for (const virtualRow of virtualRows.value) {
    touchedRowIndexes.add(virtualRow.index)
  }

  const nextSizes = new Map<number, number>()
  for (const rowIndex of touchedRowIndexes) {
    const row = rows.value[rowIndex]
    if (!row) {
      continue
    }

    if (row.kind === 'date') {
      rowVirtualizer.value.resizeItem(rowIndex, row.size)
      continue
    }

    const message = messageAt(row.messageIndex)
    if (!message) {
      rowVirtualizer.value.resizeItem(rowIndex, defaultMessageRowSize)
      continue
    }

    const size = estimateMessageRowSize(message)
    nextSizes.set(rowIndex, size)
    rowVirtualizer.value.resizeItem(rowIndex, size)
  }

  rowSizeCache.value = nextSizes
  scheduleVisibleMessagesLoad()
}

function cssPixels(value: string) {
  const parsed = Number.parseFloat(value)
  return Number.isFinite(parsed) ? parsed : 0
}

function currentViewportAnchorRowIndex() {
  const element = timelineScrollRef.value
  if (!element) {
    return -1
  }

  const containerRect = element.getBoundingClientRect()
  const targetY = containerRect.top + containerRect.height / 2
  let bestIndex = -1
  let bestDistance = Number.POSITIVE_INFINITY

  for (const row of element.querySelectorAll<HTMLElement>('.timeline-row')) {
    const rowIndex = Number(row.dataset.index)
    if (!Number.isFinite(rowIndex)) {
      continue
    }

    const rect = row.getBoundingClientRect()
    const distance = Math.abs((rect.top + rect.bottom) / 2 - targetY)
    if (distance < bestDistance) {
      bestDistance = distance
      bestIndex = rowIndex
    }
  }

  return bestIndex
}

function isPretextFlowSegment(segment: ChatExportSegment) {
  return !segment.media &&
    !segment.forwardedMessage &&
    !segment.sharedContact &&
    !segment.miniApp &&
    !segment.faceAssetPath &&
    !!segmentDisplayText(segment)
}

function isBlockFlowSegment(segment: ChatExportSegment) {
  return !!segment.media ||
    !!segment.forwardedMessage ||
    !!segment.sharedContact ||
    !!segment.miniApp
}

function segmentDisplayText(segment: ChatExportSegment) {
  return segment.displayText || segment.text || ''
}

function estimateSegmentBox(segment: ChatExportSegment, maxWidth: number) {
  if (segment.media?.kind === 'Image' && segment.media.relativePath) {
    return mediaDisplaySize(segment.media, 'Image')
  }

  if (segment.media?.kind === 'Video' && segment.media.relativePath) {
    return mediaDisplaySize(segment.media, 'Video')
  }

  if (segment.media?.kind === 'Voice') {
    return { width: 282, height: 128 }
  }

  if (segment.media?.kind === 'File') {
    return { width: 202, height: 66 }
  }

  if (segment.miniApp) {
    return { width: 360, height: 86 }
  }

  if (segment.forwardedMessage || segment.sharedContact) {
    return { width: 360, height: 82 }
  }

  if (segment.faceAssetPath) {
    return { width: 22, height: 22 }
  }

  const text = segment.displayText || segment.text || segment.media?.displayText || ''
  if (!text) {
    return null
  }

  return {
    width: estimateTextWidth(text, maxWidth),
    height: estimateTextHeight(text, maxWidth, textLineHeight),
  }
}

function estimateTextWidth(text: string, maxWidth: number, style = messageTextStyle) {
  const layoutText = normalizeTextForLayout(text)
  let width = 0
  const lines = style.whiteSpace === 'pre-wrap'
    ? layoutText.split('\n')
    : [layoutText]
  for (const line of lines) {
    width = Math.max(width, measureLineWidth(line, style))
  }

  return Math.max(1, Math.min(maxWidth, Math.ceil(width)))
}

const preparedTextCache = new Map<string, ReturnType<typeof prepareWithSegments>>()

function estimateTextHeight(
  text: string,
  maxWidth: number,
  lineHeight: number,
  style = messageTextStyle,
) {
  if (!text) {
    return 0
  }

  const result = layout(preparedText(text, style), maxWidth, lineHeight)
  return Math.max(lineHeight, result.height)
}

function measureLineWidth(text: string, style: TextMeasureStyle) {
  return measureNaturalWidth(preparedText(text, style))
}

function preparedText(text: string, style: TextMeasureStyle) {
  const layoutText = normalizeTextForLayout(text)
  const key = `${style.font}\0${style.whiteSpace}\0${layoutText}`
  let prepared = preparedTextCache.get(key)
  if (!prepared) {
    prepared = prepareWithSegments(layoutText, style.font, { whiteSpace: style.whiteSpace })
    if (preparedTextCache.size >= preparedTextCacheLimit) {
      const oldestKey = preparedTextCache.keys().next().value
      if (oldestKey !== undefined) {
        preparedTextCache.delete(oldestKey)
      }
    }
    preparedTextCache.set(key, prepared)
  }

  return prepared
}

function normalizeTextForLayout(text: string) {
  return text.replace(/\r\n/g, '\n').replace(/\r/g, ' ')
}

function mediaDisplaySize(media: ChatExportMedia | null | undefined, kind: 'Image' | 'Video') {
  const maxWidth = kind === 'Video' ? 360 : 240
  const maxHeight = kind === 'Video' ? 240 : 180
  const fallbackWidth = kind === 'Video' ? 320 : 160
  const fallbackHeight = kind === 'Video' ? 180 : 120
  const sourceWidth = media?.width && media.width > 0 ? media.width : 0
  const sourceHeight = media?.height && media.height > 0 ? media.height : 0

  if (sourceWidth <= 0 || sourceHeight <= 0) {
    return {
      width: fallbackWidth,
      height: fallbackHeight,
    }
  }

  const scale = Math.min(maxWidth / sourceWidth, maxHeight / sourceHeight, 1)
  return {
    width: Math.max(1, Math.round(sourceWidth * scale)),
    height: Math.max(1, Math.round(sourceHeight * scale)),
  }
}

function assetUrl(path?: string | null) {
  return path ? `./${path}` : ''
}

function avatarUrl(path?: string | null, remote?: string | null) {
  return assetUrl(path) || remote || ''
}

function conversationTypeLabel(type: string) {
  return type.includes('Group') || type === 'Group'
    ? '群聊'
    : type.includes('Private') || type === 'Private'
      ? '私聊'
      : type
}

function logicalLabel(conversation: ChatExportConversation) {
  if (!conversation.logicalId) {
    return conversationTypeLabel(conversation.type)
  }

  return conversation.logicalType === 'group'
    ? `群号 ${conversation.logicalId}`
    : conversation.logicalType === 'private'
      ? `QQ号 ${conversation.logicalId}`
      : conversation.logicalId
}

function sourceCountLabel(conversation: ChatExportConversation) {
  const sourceCount = conversation.sources.length
  return sourceCount > 1 ? `${sourceCount} 个数据库来源` : ''
}

function shouldShowSourceLabel() {
  const conversation = activeConversation.value
  return !!conversation && conversation.sources.length > 1
}

function messageSourceLabel(_message: ChatExportMessage) {
  return undefined
}

function chooseChatJson() {
  fileInput.value?.click()
}

async function loadSelectedChatJson(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) {
    return
  }

  try {
    documentData.value = await loadChatExportFromFile(file)
    errorMessage.value = ''
    searchText.value = ''
    searchHits.value = []
    activeSearchIndex.value = -1
    window.scrollTo({ top: 0 })
    await afterDocumentLoaded()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    input.value = ''
  }
}

function createSearchText(message: ChatExportMessage) {
  const parts: Array<string | null | undefined> = [
    message.displayText,
    message.reply?.previewText,
    message.systemHint?.action,
    message.systemHint?.sourceName,
    message.systemHint?.targetName,
  ]

  for (const segment of message.segments) {
    parts.push(segment.displayText, segment.text)
    if (segment.media) {
      parts.push(segment.media.displayText, segment.media.fileName)
    }
    if (segment.forwardedMessage) {
      parts.push(segment.forwardedMessage.title, segment.forwardedMessage.footer, ...segment.forwardedMessage.previewLines)
    }
    if (segment.sharedContact) {
      parts.push(segment.sharedContact.title, segment.sharedContact.subtitle, segment.sharedContact.tag)
    }
    if (segment.miniApp) {
      parts.push(segment.miniApp.appName, segment.miniApp.title, segment.miniApp.hostName)
    }
  }

  return parts.filter(Boolean).join('\n')
}

function createSearchPreviewText(message: ChatExportMessage) {
  const text = message.displayText || message.systemHint?.action || message.segments
    .map((segment) => segment.displayText || segment.text)
    .filter(Boolean)
    .join(' ')

  return text.replace(/\s+/g, ' ').trim() || '[消息]'
}

function scheduleSearch() {
  window.clearTimeout(searchTimer)
  searchRequestId += 1
  isSearching.value = false
  searchTimer = window.setTimeout(runSearch, searchDebounceMilliseconds)
}

function runSearch() {
  const query = searchText.value.trim()
  searchRequestId += 1
  const requestId = searchRequestId

  if (!query) {
    searchHits.value = []
    activeSearchIndex.value = -1
    searchStatus.value = ''
    isSearching.value = false
    return
  }

  searchHits.value = []
  activeSearchIndex.value = -1
  isSearching.value = true
  searchStatus.value = '正在搜索...'
  void searchInBatches(requestId, query)
}

async function searchInBatches(requestId: number, query: string) {
  const chat = activeChat.value
  const normalizedQuery = normalizeSearchText(query)
  if (!chat || !normalizedQuery) {
    applySearchResults(requestId, query, [])
    return
  }

  const hits: SearchHit[] = []
  const total = chat.messageCount ?? chat.messages?.length ?? 0
  for (let index = 0; index < total;) {
    if (requestId !== searchRequestId || query !== searchText.value.trim()) {
      isSearching.value = false
      return
    }

    const messages = await loadMessagesForLookup(index)
    if (requestId !== searchRequestId || query !== searchText.value.trim()) {
      isSearching.value = false
      return
    }

    const chunk = chat.messageChunks?.find((item) =>
      index >= item.start && index < item.start + item.count,
    )
    const baseIndex = chat.messages ? 0 : chunk?.start ?? index
    const startOffset = Math.max(0, index - baseIndex)
    for (let offset = startOffset; offset < messages.length && index < total; offset++, index++) {
      const message = messages[offset]
      if (message && normalizeSearchText(createSearchText(message)).includes(normalizedQuery)) {
        hits.push({
          key: message.key,
          index,
          preview: createPreview(createSearchPreviewText(message), query),
        })
      }
    }

    if (messages.length === 0) {
      index++
    }

    applySearchResults(requestId, query, hits.slice(), false, index, total)
    await nextFrame()
  }

  applySearchResults(requestId, query, hits, true, total, total)
}

async function openSearch() {
  isSettingsOpen.value = false
  isSearchOpen.value = true
  await nextTick()
  searchInputRef.value?.focus()
  searchInputRef.value?.select()
}

function toggleSettings() {
  isSettingsOpen.value = !isSettingsOpen.value
  if (isSettingsOpen.value) {
    closeSearch()
  }
}

function closeSearch() {
  cancelSearch()
  isSearchOpen.value = false
}

function cancelSearch() {
  window.clearTimeout(searchTimer)
  searchRequestId += 1
  isSearching.value = false
  if (searchText.value.trim()) {
    searchStatus.value = searchHits.value.length > 0 ? `已取消, 已找到 ${searchHits.value.length} 条` : '已取消'
  }
}

function handleGlobalKeyDown(event: KeyboardEvent) {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'f') {
    event.preventDefault()
    void openSearch()
    return
  }

  if (event.key === 'Escape' && isSearchOpen.value) {
    event.preventDefault()
    closeSearch()
    return
  }

  if (isTextInputActive()) {
    return
  }

  if (event.key === 'Home' || event.key === 'End') {
    event.preventDefault()
    if (event.repeat) {
      return
    }

    if (event.key === 'Home') {
      void scrollToWindowTop()
      return
    }

    void scrollToWindowBottom()
  }
}

function handleTimelineWheel(event: WheelEvent) {
  if (!event.altKey) {
    return
  }

  event.preventDefault()
  timelineScrollElement()?.scrollBy({
    top: event.deltaY * 3,
    left: event.deltaX * 3,
    behavior: 'auto',
  })
}

function isTextInputActive() {
  const element = document.activeElement
  return (
    element instanceof HTMLInputElement ||
    element instanceof HTMLTextAreaElement ||
    (element instanceof HTMLElement && element.isContentEditable)
  )
}

function applySearchResults(
  requestId: number,
  query: string,
  hits: SearchHit[],
  done = true,
  scanned = 0,
  total = 0,
) {
  if (requestId !== searchRequestId || query !== searchText.value.trim()) {
    return
  }

  searchHits.value = hits
  activeSearchIndex.value = hits.length > 0 ? Math.max(0, activeSearchIndex.value) : -1
  isSearching.value = !done
  if (!done) {
    searchStatus.value = `正在搜索... ${scanned}/${total}, 已找到 ${hits.length} 条`
    return
  }

  searchStatus.value = hits.length > 0 ? `找到 ${hits.length} 条` : '没有找到'
}

function jumpToSearchHit(index: number) {
  const hit = searchHits.value[index]
  if (!hit) {
    return
  }

  activeSearchIndex.value = index
  void jumpToMessageIndex(hit.index, hit.key)
}

async function jumpToReply(reply: ChatExportReply, sourceMessage?: ChatExportMessage) {
  const target = await findReplyTarget(reply, sourceMessage?.raw.conversationKey)
  if (target) {
    await jumpToMessageIndex(target.index, target.message.key, {
      behavior: target.wasLoaded ? 'smooth' : 'auto',
      transientHighlight: true,
    })
  }
}

async function findReplyTarget(reply: ChatExportReply, conversationKey?: string) {
  const chat = activeChat.value
  const total = chat?.messageCount ?? chat?.messages?.length ?? 0
  if (!chat || total <= 0) {
    return null
  }

  const indexedTarget =
    lookupReplyIndex(replyIndexBySeq, conversationKey, reply.messageSeq) ??
    lookupReplyIndex(replyIndexBySeq, conversationKey, reply.alternateMessageSeq) ??
    lookupReplyIndex(replyIndexByMessageId, conversationKey, reply.messageId) ??
    lookupReplyIndex(replyIndexByMessageId, conversationKey, reply.internalMessageId) ??
    lookupReplyIndex(replyIndexByRandom, conversationKey, reply.messageRandom)
  if (indexedTarget) {
    const wasLoaded = !!messageAt(indexedTarget.index)
    await ensureMessageLoaded(indexedTarget.index)
    const message = messageAt(indexedTarget.index)
    return message
      ? { index: indexedTarget.index, message, wasLoaded }
      : { index: indexedTarget.index, message: indexedTarget, wasLoaded }
  }

  for (let index = 0; index < total;) {
    const messages = await loadMessagesForLookup(index)
    const chunk = chat.messageChunks?.find((item) =>
      index >= item.start && index < item.start + item.count,
    )
    const baseIndex = chat.messages ? 0 : chunk?.start ?? index

    for (let offset = Math.max(0, index - baseIndex); offset < messages.length && index < total; offset++, index++) {
      const message = messages[offset]
      if (message && isReplyTarget(message, reply, conversationKey)) {
        return { index, message, wasLoaded: !!messageAt(index) }
      }
    }

    if (messages.length === 0) {
      index++
    }

    await nextFrame()
  }

  return null
}

function lookupReplyIndex(
  map: Map<string, ChatExportMessageIndex>,
  conversationKey: string | undefined,
  value: number,
) {
  if (value === 0) {
    return undefined
  }

  return map.get(replyLookupKey(conversationKey, value)) ?? map.get(replyLookupKey('', value))
}

function isReplyIndexTarget(
  message: Pick<ChatExportMessage, 'messageId' | 'messageRandom' | 'messageSeq'>,
  reply: ChatExportReply,
) {
  return (reply.messageSeq !== 0 && message.messageSeq === reply.messageSeq) ||
    (reply.alternateMessageSeq !== 0 && message.messageSeq === reply.alternateMessageSeq) ||
    (reply.messageId !== 0 && message.messageId === reply.messageId) ||
    (reply.internalMessageId !== 0 && message.messageId === reply.internalMessageId) ||
    (reply.messageRandom !== 0 && message.messageRandom === reply.messageRandom)
}

function isReplyTarget(message: ChatExportMessage, reply: ChatExportReply, conversationKey?: string) {
  return (!conversationKey || message.raw.conversationKey === conversationKey) && isReplyIndexTarget(message, reply)
}

function jumpToPreviousHit() {
  if (searchHits.value.length === 0) {
    return
  }

  const nextIndex = activeSearchIndex.value <= 0 ? searchHits.value.length - 1 : activeSearchIndex.value - 1
  jumpToSearchHit(nextIndex)
}

function jumpToNextHit() {
  if (searchHits.value.length === 0) {
    return
  }

  const nextIndex = activeSearchIndex.value >= searchHits.value.length - 1 ? 0 : activeSearchIndex.value + 1
  jumpToSearchHit(nextIndex)
}

async function jumpToMessage(messageKey: string) {
  const rowIndex = rows.value.findIndex((row) => {
    if (row.kind !== 'message') {
      return false
    }

    return row.key === messageKey || messageAt(row.messageIndex)?.key === messageKey
  })
  if (rowIndex < 0) {
    return
  }

  await scrollToRowIndex(rowIndex, messageKey, 'center', 'auto')
}

async function jumpToMessageIndex(
  messageIndex: number,
  messageKey: string,
  options: { behavior?: ScrollBehavior; transientHighlight?: boolean } = {},
) {
  const rowIndex = messageIndexToRowIndex(messageIndex)
  if (rowIndex < 0) {
    await jumpToMessage(messageKey)
    return
  }

  await scrollToRowIndex(rowIndex, messageKey, 'center', options.behavior ?? 'auto')
  if (options.transientHighlight) {
    showTransientHighlight(messageKey)
  }
}

function scrollToWindowTop() {
  const element = timelineScrollElement()
  element?.scrollBy({ top: -element.clientHeight * keyScrollStepRatio, behavior: 'auto' })
}

function scrollToWindowBottom() {
  const element = timelineScrollElement()
  element?.scrollBy({ top: element.clientHeight * keyScrollStepRatio, behavior: 'auto' })
}

async function scrollToRowIndex(
  index: number,
  messageKey: string,
  align: 'start' | 'center' | 'end',
  behavior: ScrollBehavior,
) {
  const row = rows.value[index]
  if (row?.kind === 'message') {
    await ensureMessageLoaded(row.messageIndex)
    ensureMessageRowSize(row.messageIndex)
  }

  if (!isVirtualizationEnabled.value) {
    await nextFrame()
    centerRenderedMessage(messageKey, behavior)
    return
  }

  rowVirtualizer.value.scrollToIndex(index, { align, behavior })
  await nextFrame()
  rowVirtualizer.value.scrollToIndex(index, { align, behavior })
  await nextFrame()
  centerRenderedMessage(messageKey, behavior)
}

function centerRenderedMessage(messageKey: string, behavior: ScrollBehavior) {
  const element = timelineRef.value?.querySelector<HTMLElement>(`[data-message-key="${cssEscape(messageKey)}"] .message`)
  if (!element) {
    return
  }

  const scrollElement = timelineScrollElement()
  const rect = element.getBoundingClientRect()
  const containerRect = scrollElement?.getBoundingClientRect()
  if (scrollElement && containerRect) {
    scrollElement.scrollBy({
      top: rect.top + rect.height / 2 - containerRect.top - scrollElement.clientHeight / 2,
      behavior,
    })
    return
  }

  window.scrollBy({ top: rect.top + rect.height / 2 - window.innerHeight / 2, behavior })
}

function showTransientHighlight(messageKey: string) {
  transientHighlightKey.value = messageKey
  window.clearTimeout(transientHighlightTimer)
  transientHighlightTimer = window.setTimeout(() => {
    if (transientHighlightKey.value === messageKey) {
      transientHighlightKey.value = ''
    }
  }, 1500)
}

function timelineScrollElement() {
  return timelineScrollRef.value
}

function stickToTimelineBottom() {
  const element = timelineScrollElement()
  if (!element) {
    return
  }

  element.scrollTop = Math.max(0, element.scrollHeight - element.clientHeight)
}

function nextFrame() {
  return new Promise<void>((resolve) => window.requestAnimationFrame(() => resolve()))
}

function cssEscape(value: string) {
  return typeof CSS !== 'undefined' && typeof CSS.escape === 'function'
    ? CSS.escape(value)
    : value.replace(/["\\]/g, '\\$&')
}

function normalizeSearchText(value: string) {
  return value.normalize('NFKC').toLowerCase().replace(/\s+/g, ' ').trim()
}

function createPreview(text: string, query: string) {
  const source = text.replace(/\s+/g, ' ').trim()
  const lowerSource = source.toLowerCase()
  const lowerQuery = query.toLowerCase()
  const pos = lowerSource.indexOf(lowerQuery)
  const start = Math.max(0, pos < 0 ? 0 : pos - 24)
  const limit = 56
  const preview = source.slice(start, start + limit)
  return `${start > 0 ? '...' : ''}${preview}${start + limit < source.length ? '...' : ''}`
}

function chatAvatarText(conversation: ChatExportConversation) {
  return conversation.title.slice(0, 1)
}

function chatMessageCount(chat: ChatExportChat) {
  return chat.messageCount ?? chat.messages?.length ?? 0
}

function selectChat(chat: ChatExportChat) {
  if (chat.key === activeChatKey.value) {
    return
  }

  void activateChat(chat.key)
}

function startConversationListResize(event: PointerEvent) {
  if (event.button !== 0) {
    return
  }

  const rect = conversationListRef.value?.getBoundingClientRect()
  if (!rect) {
    return
  }

  conversationListResizeLeft = rect.left
  conversationListWidthBeforeResize = conversationListWidth.value
  isConversationListResizing.value = true
  setDocumentResizeCursor(true)
  window.addEventListener('pointermove', resizeConversationList)
  window.addEventListener('pointerup', finishConversationListResize)
  window.addEventListener('pointercancel', finishConversationListResize)
  event.preventDefault()
}

function resizeConversationList(event: PointerEvent) {
  if (!isConversationListResizing.value) {
    return
  }

  const rawWidth = event.clientX - conversationListResizeLeft
  if (rawWidth <= collapseConversationListThreshold) {
    collapseConversationList()
    finishConversationListResize()
    return
  }

  isConversationListCollapsed.value = false
  conversationListWidth.value = Math.round(Math.min(maxConversationListWidth, Math.max(minConversationListWidth, rawWidth)))
}

function finishConversationListResize() {
  if (!isConversationListResizing.value) {
    return
  }

  isConversationListResizing.value = false
  if (!isConversationListCollapsed.value) {
    lastExpandedConversationListWidth = conversationListWidth.value
  }
  setDocumentResizeCursor(false)
  window.removeEventListener('pointermove', resizeConversationList)
  window.removeEventListener('pointerup', finishConversationListResize)
  window.removeEventListener('pointercancel', finishConversationListResize)
}

function collapseConversationList() {
  lastExpandedConversationListWidth = Math.max(minConversationListWidth, conversationListWidthBeforeResize)
  isConversationListCollapsed.value = true
}

function expandConversationList() {
  conversationListWidth.value = Math.round(Math.min(maxConversationListWidth, Math.max(minConversationListWidth, lastExpandedConversationListWidth)))
  isConversationListCollapsed.value = false
}

function setDocumentResizeCursor(enabled: boolean) {
  if (enabled) {
    documentResizeCursor = document.body.style.cursor
    documentResizeUserSelect = document.body.style.userSelect
    document.body.style.cursor = 'col-resize'
    document.body.style.userSelect = 'none'
    return
  }

  document.body.style.cursor = documentResizeCursor
  document.body.style.userSelect = documentResizeUserSelect
}
</script>

<template>
  <main
    v-if="documentData"
    class="viewer"
    :class="{
      'merged-viewer': isMergedExport,
      'conversation-list-collapsed': isConversationListCollapsed,
      'conversation-list-resizing': isConversationListResizing,
    }"
    :style="viewerStyle"
  >
    <aside v-if="isMergedExport" ref="conversationListRef" class="conversation-list">
      <template v-if="!isConversationListCollapsed">
        <div class="conversation-list-head">
          <strong>聊天记录</strong>
          <span>{{ exportedChats.length }} 个聊天</span>
        </div>
        <div class="conversation-list-scroll">
          <button
            v-for="chat in exportedChats"
            :key="chat.key"
            type="button"
            class="conversation-list-item"
            :class="{ active: chat.key === activeChatKey }"
            @click="selectChat(chat)"
          >
            <span class="conversation-list-avatar">
              <img
                v-if="avatarUrl(chat.conversation.avatarPath, chat.conversation.avatarUrl)"
                :src="avatarUrl(chat.conversation.avatarPath, chat.conversation.avatarUrl)"
                loading="lazy"
                decoding="async"
                alt=""
              />
              <span v-else>{{ chatAvatarText(chat.conversation) }}</span>
            </span>
            <span class="conversation-list-main">
              <strong>{{ chat.conversation.title }}</strong>
              <small>{{ chatMessageCount(chat) }} 条消息</small>
            </span>
          </button>
        </div>
        <div
          class="conversation-list-resizer"
          role="separator"
          aria-orientation="vertical"
          aria-label="拖拽调整聊天列表宽度"
          :aria-valuemin="minConversationListWidth"
          :aria-valuemax="maxConversationListWidth"
          :aria-valuenow="conversationListWidth"
          title="拖拽调整宽度"
          @pointerdown="startConversationListResize"
        ></div>
      </template>
      <button
        v-else
        type="button"
        class="conversation-list-expand"
        aria-label="展开聊天列表"
        title="展开聊天列表"
        @click="expandConversationList"
      ></button>
    </aside>

    <header v-if="activeConversation" class="conversation-header">
      <div class="conversation-avatar">
        <img
          v-if="avatarUrl(activeConversation.avatarPath, activeConversation.avatarUrl)"
          :src="avatarUrl(activeConversation.avatarPath, activeConversation.avatarUrl)"
          loading="lazy"
          decoding="async"
          alt=""
        />
        <span v-else>{{ chatAvatarText(activeConversation) }}</span>
      </div>
      <div class="conversation-summary">
        <h1>{{ activeConversation.title }}</h1>
        <p>
          {{ activeMessageCount }} 条消息 · {{ logicalLabel(activeConversation) }}
        </p>
        <p v-if="sourceCountLabel(activeConversation)">
          {{ sourceCountLabel(activeConversation) }}
        </p>
      </div>
      <div class="header-actions">
        <button type="button" class="header-action-button" @click="openSearch">搜索</button>
        <button type="button" class="header-action-button" @click="toggleSettings">设置</button>
      </div>
    </header>

    <section ref="timelineRef" class="timeline">
      <div
        ref="timelineScrollRef"
        class="timeline-scroller"
        :class="{ 'full-timeline-scroller': !isVirtualizationEnabled }"
        @wheel="handleTimelineWheel"
      >
        <div
          v-if="isVirtualizationEnabled"
          class="timeline-virtual-spacer"
          :style="{ height: `${timelineHeight}px` }"
        >
          <div
            v-for="{ virtualRow, row } in renderedRows"
            :key="row.key"
            class="timeline-row"
            :class="row.kind === 'date' ? 'date-row' : 'message-row'"
            :data-index="virtualRow.index"
            :data-message-key="row.kind === 'message' ? row.message.key : undefined"
            :style="{ height: `${row.size}px`, transform: `translateY(${virtualRow.start}px)` }"
          >
            <template v-if="row.kind === 'date'">
              <div class="date-divider">
                {{ row.label }}
              </div>
            </template>
            <MessageItem
              v-else
              :message="row.message"
              :show-source="shouldShowSourceLabel()"
              :source-label="messageSourceLabel(row.message)"
              :highlighted="currentHit?.key === row.message.key || transientHighlightKey === row.message.key"
              @reply-click="jumpToReply"
            />
          </div>
        </div>
        <div v-else class="timeline-full-list">
          <div
            v-for="{ rowIndex, row } in fullRows"
            :key="row.key"
            class="timeline-row full-timeline-row"
            :class="row.kind === 'date' ? 'date-row' : 'message-row'"
            :data-index="rowIndex"
            :data-message-key="row.kind === 'message' ? row.message.key : undefined"
          >
            <template v-if="row.kind === 'date'">
              <div class="date-divider">
                {{ row.label }}
              </div>
            </template>
            <MessageItem
              v-else
              :message="row.message"
              :show-source="shouldShowSourceLabel()"
              :source-label="messageSourceLabel(row.message)"
              :highlighted="currentHit?.key === row.message.key || transientHighlightKey === row.message.key"
              @reply-click="jumpToReply"
            />
          </div>
          <div v-if="isLoadingAllMessages" class="load-all-status">正在加载全部消息...</div>
        </div>
      </div>
    </section>

    <aside v-if="isSettingsOpen" class="floating-settings">
      <div class="floating-settings-head">
        <strong>设置</strong>
        <button type="button" class="icon-button" aria-label="关闭设置" @click="isSettingsOpen = false">×</button>
      </div>
      <button type="button" class="settings-toggle-button" :disabled="isLoadingAllMessages" @click="toggleVirtualization">
        {{ isVirtualizationEnabled ? '关闭虚拟化' : '开启虚拟化' }}
      </button>
      <p v-if="!isVirtualizationEnabled">已关闭虚拟化，切换聊天后仍保持关闭。</p>
    </aside>

    <aside v-if="isSearchOpen" class="floating-search">
      <div class="floating-search-head">
        <label class="search-box">
          <span>搜索聊天记录</span>
          <input
            ref="searchInputRef"
            v-model="searchText"
            type="search"
            placeholder="输入关键词"
          />
        </label>
        <button type="button" class="icon-button" aria-label="关闭搜索" @click="closeSearch">×</button>
      </div>
      <div class="search-actions">
        <button type="button" :disabled="searchHits.length === 0" @click="jumpToPreviousHit">上一条</button>
        <button type="button" :disabled="searchHits.length === 0" @click="jumpToNextHit">下一条</button>
        <button type="button" :disabled="!isSearching" @click="cancelSearch">取消</button>
        <span>{{ hasSearch ? searchStatus : '按 Ctrl+F 搜索聊天记录' }}</span>
      </div>
      <div v-if="hasSearch && searchHits.length > 0" class="search-results">
        <button
          v-for="(hit, index) in searchHits"
          :key="hit.key"
          type="button"
          :class="{ active: index === activeSearchIndex }"
          @click="jumpToSearchHit(index)"
        >
          <strong>#{{ hit.index + 1 }}</strong>
          <span>{{ hit.preview }}</span>
        </button>
      </div>
    </aside>
  </main>

  <main v-else class="loading">
    <div v-if="errorMessage" class="load-error">
      <p>{{ errorMessage }}</p>
      <button type="button" @click="chooseChatJson">选择 chat.json</button>
      <input
        ref="fileInput"
        type="file"
        accept="application/json,.json"
        @change="loadSelectedChatJson"
      />
    </div>
    <p v-else>正在加载聊天记录...</p>
  </main>
</template>
