export type ConversationType =
  | 'Group'
  | 'Private'
  | 'PCQQGroup'
  | 'PCQQPrivate'
  | 'AndroidMobileQQGroup'
  | 'AndroidMobileQQPrivate'
  | 'Icalingua'

export type SegmentType =
  | 'Text'
  | 'QQFace'
  | 'Image'
  | 'Voice'
  | 'Video'
  | 'File'
  | 'ForwardedMessage'
  | 'SharedContact'
  | 'MiniApp'
  | 'Unsupported'

export type SegmentTone = 'Normal' | 'Warning' | 'Mention'

export type MediaKind = 'Image' | 'Voice' | 'Video' | 'File'

export interface ChatExportDocument {
  schemaVersion: string
  metadata: ChatExportMetadata
  conversation: ChatExportConversation
  participants: ChatExportParticipant[]
  messages?: ChatExportMessage[] | null
  messageChunks?: ChatExportMessageChunk[]
  timelineDates?: ChatExportTimelineDate[]
  messageIndex?: ChatExportMessageIndex[]
}

export interface ChatExportMessageChunk {
  index: number
  start: number
  count: number
  path: string
}

export interface ChatExportTimelineDate {
  rowIndex: number
  messageIndex: number
  label: string
}

export interface ChatExportMessageIndex {
  index: number
  key: string
  messageId: number
  messageRandom: number
  messageSeq: number
}

export interface ChatExportMetadata {
  exportedAt: string
  exporter: string
  viewer: string
  messageCount: number
}

export interface ChatExportConversation {
  key: string
  type: ConversationType
  title: string
  avatarUrl?: string | null
  avatarPath?: string | null
  logicalType: string
  logicalId: string
  sources: ChatExportConversationSource[]
  groupId: number
  privateConversationId: number
  privateUin: number
  privateUid?: string | null
  androidMobileQQPeerUin?: string | null
  icalinguaRoomId: number
}

export interface ChatExportConversationSource {
  key: string
  type: ConversationType
  title: string
  groupId: number
  privateConversationId: number
  privateUin: number
  privateUid?: string | null
  androidMobileQQPeerUin?: string | null
  icalinguaRoomId: number
}

export interface ChatExportParticipant {
  key: string
  uin: number
  uid?: string | null
  displayName: string
  avatarUrl?: string | null
  avatarPath?: string | null
}

export interface ChatExportMessage {
  key: string
  messageId: number
  messageRandom: number
  messageSeq: number
  pcqqMessageSeq: number
  messageTime: number
  localTime: string
  sender: ChatExportParticipantRef
  isSystemHint: boolean
  isRecalled: boolean
  displayText: string
  reply?: ChatExportReply | null
  segments: ChatExportSegment[]
  reactions: ChatExportReaction[]
  forwardedMessages: ChatExportMessage[]
  systemHint?: ChatExportSystemHint | null
  raw: ChatExportRawMessage
}

export interface ChatExportParticipantRef {
  key: string
  uin: number
  uid?: string | null
  displayName: string
  avatarUrl?: string | null
  avatarPath?: string | null
}

export interface ChatExportReply {
  messageId: number
  internalMessageId: number
  messageRandom: number
  rawMessageId: string
  messageSeq: number
  alternateMessageSeq: number
  senderId: number
  senderName: string
  messageTime: number
  sourceGroupId: number
  sourceGroupName: string
  previewText: string
  segments: ChatExportSegment[]
}

export interface ChatExportReaction {
  faceId: string
  count: number
  displayText: string
  faceAssetPath?: string | null
}

export interface ChatExportSystemHint {
  sourceName: string
  sourceUin: string
  sourceIsUser: boolean
  targetName: string
  targetUin: string
  targetIsUser: boolean
  action: string
  suffix: string
  actionImageUrl?: string | null
  targetMessageSeq: number
  faceId: number
  faceAssetPath?: string | null
}

export interface ChatExportSegment {
  type: SegmentType
  tone: SegmentTone
  text: string
  displayText: string
  linkUrl?: string | null
  isMention: boolean
  mentionUid?: string | null
  faceId?: number | null
  faceName?: string | null
  faceAssetPath?: string | null
  media?: ChatExportMedia | null
  forwardedMessage?: ChatExportForwardedCard | null
  sharedContact?: ChatExportSharedContactCard | null
  miniApp?: ChatExportMiniAppCard | null
}

export interface ChatExportMedia {
  kind: MediaKind
  isAvailable: boolean
  fileName?: string | null
  relativePath?: string | null
  coverRelativePath?: string | null
  width?: number | null
  height?: number | null
  maxWidth?: number | null
  maxHeight?: number | null
  durationMilliseconds?: number | null
  fileSize?: number | null
  displayText: string
}

export interface ChatExportForwardedCard {
  title: string
  footer: string
  previewLines: string[]
  resid?: string | null
  uniseq?: string | null
  fileName?: string | null
  messageCount?: number | null
  rawPayload: string
}

export interface ChatExportSharedContactCard {
  kind: 'Friend' | 'Group'
  title: string
  subtitle: string
  tag: string
  avatarUrl?: string | null
  jumpUrl?: string | null
  rawPayload: string
}

export interface ChatExportMiniAppCard {
  kind: 'Generic' | 'Bilibili'
  appName: string
  title: string
  hostName?: string | null
  iconUrl?: string | null
  previewUrl?: string | null
  jumpUrl?: string | null
  rawPayload: string
}

export interface ChatExportRawMessage {
  conversationType: ConversationType
  conversationKey: string
  payloadBase64?: string | null
  source?: string | null
  messageType: number
  subMessageType: number
  sendType: number
  senderUid: string
  peerUid: string
  peerUin: number
  groupId: number
  privateConversationId: number
  replyToMessageSeq: number
  contentBase64?: string | null
  subContentBase64?: string | null
  messageReactionsBase64?: string | null
}

declare global {
  interface Window {
    __QQ_DATABASE_EXPLORER_CHAT_EXPORT__?: ChatExportDocument
    __QQ_DATABASE_EXPLORER_CHAT_EXPORT_MESSAGE_CHUNKS__?: Record<number, ChatExportMessage[] | undefined>
  }
}
