<script setup lang="ts">
import type { ChatExportMedia, ChatExportMessage, ChatExportSegment } from '@/types/chat-export'

defineProps<{
  message: ChatExportMessage
  compact?: boolean
  showSource?: boolean
  highlighted?: boolean
}>()

const emit = defineEmits<{
  replyClick: [reply: NonNullable<ChatExportMessage['reply']>]
}>()

function assetUrl(path?: string | null) {
  return path ? `./${path}` : ''
}

function avatarUrl(path?: string | null, remote?: string | null) {
  return assetUrl(path) || remote || ''
}

function formatDuration(milliseconds?: number | null) {
  if (!milliseconds || milliseconds <= 0) {
    return ''
  }

  const seconds = Math.max(1, Math.round(milliseconds / 1000))
  return seconds < 60 ? `${seconds}"` : `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
}

function formatFileSize(size?: number | null) {
  if (!size || size <= 0) {
    return ''
  }

  const units = ['B', 'KB', 'MB', 'GB']
  let value = size
  let unitIndex = 0
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024
    unitIndex += 1
  }

  return `${value.toFixed(value >= 10 || unitIndex === 0 ? 0 : 1)} ${units[unitIndex]}`
}

function handleImageError(event: Event) {
  const image = event.currentTarget as HTMLImageElement | null
  image?.classList.add('load-failed')
}

function segmentKey(segment: ChatExportSegment, index: number) {
  return `${segment.type}:${segment.displayText}:${segment.media?.relativePath ?? ''}:${index}`
}

function segmentDisplayText(segment: ChatExportSegment) {
  return segment.displayText || segment.text || ''
}

function isTextFlowSegment(segment: ChatExportSegment) {
  return !segment.media &&
    !segment.forwardedMessage &&
    !segment.sharedContact &&
    !segment.miniApp &&
    !segment.faceAssetPath &&
    !!segmentDisplayText(segment)
}

function isTextFlowMessage(message: ChatExportMessage) {
  return message.segments.length > 0 && message.segments.every(isTextFlowSegment)
}

function isBlockFlowSegment(segment: ChatExportSegment) {
  return !!segment.media ||
    !!segment.forwardedMessage ||
    !!segment.sharedContact ||
    !!segment.miniApp
}

function isBlockFlowMessage(message: ChatExportMessage) {
  return !isTextFlowMessage(message) && message.segments.some(isBlockFlowSegment)
}

function sourceLabel(message: ChatExportMessage) {
  switch (message.raw.conversationType) {
    case 'Group':
    case 'Private':
      return 'QQNT'
    case 'PCQQGroup':
    case 'PCQQPrivate':
      return 'PCQQ'
    case 'AndroidMobileQQGroup':
    case 'AndroidMobileQQPrivate':
      return 'AndroidQQ'
    case 'Icalingua':
      return 'Icalingua'
    default:
      return message.raw.conversationType
  }
}

function shouldRenderSystemHint(message: ChatExportMessage) {
  return message.isSystemHint || (message.raw.messageType === 5 && (message.raw.subMessageType === 11 || message.raw.subMessageType === 12))
}

function systemHintText(message: ChatExportMessage) {
  return message.systemHint?.action || message.displayText
}

function isVisualMediaOnlyMessage(message: ChatExportMessage) {
  if (message.reply || message.forwardedMessages.length > 0 || message.segments.length !== 1) {
    return false
  }

  const media = message.segments[0]?.media
  return !!media?.relativePath && (media.kind === 'Image' || media.kind === 'Video')
}

function mediaFrameStyle(media: ChatExportMedia, kind: 'Image' | 'Video') {
  const size = mediaDisplaySize(media, kind)
  return {
    '--media-render-width': `${size.width}px`,
    '--media-render-height': `${size.height}px`,
  }
}

function mediaDisplaySize(media: ChatExportMedia, kind: 'Image' | 'Video') {
  const maxWidth = kind === 'Video' ? 360 : 240
  const maxHeight = kind === 'Video' ? 240 : 180
  const fallbackWidth = kind === 'Video' ? 320 : 160
  const fallbackHeight = kind === 'Video' ? 180 : 120
  const sourceWidth = media.width && media.width > 0 ? media.width : 0
  const sourceHeight = media.height && media.height > 0 ? media.height : 0

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
</script>

<template>
  <article class="message" :class="{ system: shouldRenderSystemHint(message), recalled: message.isRecalled, compact, highlighted }">
    <template v-if="shouldRenderSystemHint(message)">
      <div class="system-pill">
        <span v-if="message.systemHint?.sourceName" class="system-user">
          {{ message.systemHint.sourceName }}
        </span>
        <img
          v-if="message.systemHint?.actionImageUrl"
          class="system-action-image"
          :src="message.systemHint.actionImageUrl"
          alt=""
        />
        <span>{{ systemHintText(message) }}</span>
        <span v-if="message.systemHint?.targetName" class="system-user">
          {{ message.systemHint.targetName }}
        </span>
        <img
          v-if="message.systemHint?.faceAssetPath"
          class="system-action-image"
          :src="assetUrl(message.systemHint.faceAssetPath)"
          alt=""
        />
        <span v-if="message.systemHint?.suffix">{{ message.systemHint.suffix }}</span>
      </div>
    </template>

    <template v-else>
      <div class="message-avatar">
          <img
            v-if="avatarUrl(message.sender.avatarPath, message.sender.avatarUrl)"
            :src="avatarUrl(message.sender.avatarPath, message.sender.avatarUrl)"
            loading="lazy"
            alt=""
          />
        <span v-else>{{ message.sender.displayName.slice(0, 1) }}</span>
      </div>

      <div class="message-main">
        <div class="message-head">
          <span class="sender-name">{{ message.sender.displayName }}</span>
          <span v-if="message.isRecalled" class="recalled-label">已撤回</span>
          <span v-if="showSource" class="source-label">{{ sourceLabel(message) }}</span>
          <time>{{ message.localTime.slice(11) }}</time>
        </div>

        <div class="bubble" :class="{ 'visual-media-bubble': isVisualMediaOnlyMessage(message) }">
          <button
            v-if="message.reply"
            type="button"
            class="reply"
            tabindex="-1"
            @click="emit('replyClick', message.reply)"
          >
            <div class="reply-sender">{{ message.reply.senderName }}</div>
            <div class="reply-text">{{ message.reply.previewText }}</div>
          </button>

          <div class="segments" :class="{ 'text-flow': isTextFlowMessage(message), 'block-flow': isBlockFlowMessage(message) }">
            <template v-for="(segment, index) in message.segments" :key="segmentKey(segment, index)">
              <a
                v-if="segment.linkUrl"
                class="text-segment link"
                :class="segment.tone.toLowerCase()"
                :href="segment.linkUrl"
                target="_blank"
                rel="noreferrer"
                tabindex="-1"
              >
                {{ segment.displayText }}
              </a>
              <span
                v-else-if="segment.type === 'Text' || segment.type === 'Unsupported'"
                class="text-segment"
                :class="segment.tone.toLowerCase()"
              >
                {{ segment.displayText }}
              </span>
              <span v-else-if="segment.type === 'QQFace'" class="face-segment">
                <img v-if="segment.faceAssetPath" :src="assetUrl(segment.faceAssetPath)" alt="" />
                <span v-else>{{ segment.displayText }}</span>
              </span>
              <a
                v-else-if="segment.media?.kind === 'Image' && segment.media.relativePath"
                class="image-link"
                :style="mediaFrameStyle(segment.media, 'Image')"
                :href="assetUrl(segment.media.relativePath)"
                target="_blank"
                tabindex="-1"
              >
                <img
                  class="media-image"
                  :src="assetUrl(segment.media.relativePath)"
                  loading="lazy"
                  decoding="async"
                  :alt="segment.media.displayText"
                  @error="handleImageError"
                />
                <span class="image-error">{{ segment.media.displayText || '[图片加载失败]' }}</span>
              </a>
              <span v-else-if="segment.media?.kind === 'Image'" class="text-segment warning">
                {{ segment.media.displayText }}
              </span>
              <div v-else-if="segment.media?.kind === 'Voice'" class="voice-card">
                <audio v-if="segment.media.relativePath" controls :src="assetUrl(segment.media.relativePath)" tabindex="-1" />
                <span>{{ segment.media.displayText }}</span>
                <span>{{ formatDuration(segment.media.durationMilliseconds) }}</span>
              </div>
              <div v-else-if="segment.media?.kind === 'Video'" class="video-card">
                <video
                  v-if="segment.media.relativePath"
                  controls
                  preload="metadata"
                  :style="mediaFrameStyle(segment.media, 'Video')"
                  :poster="assetUrl(segment.media.coverRelativePath)"
                  :src="assetUrl(segment.media.relativePath)"
                  tabindex="-1"
                />
                <span v-else class="text-segment warning">{{ segment.media.displayText }}</span>
              </div>
              <a
                v-else-if="segment.media?.kind === 'File' && segment.media.relativePath"
                class="file-card"
                :href="assetUrl(segment.media.relativePath)"
                target="_blank"
                tabindex="-1"
              >
                <span>{{ segment.media.fileName || segment.media.displayText }}</span>
                <small>{{ formatFileSize(segment.media.fileSize) }}</small>
              </a>
              <span v-else-if="segment.media?.kind === 'File'" class="text-segment warning">
                {{ segment.media.displayText }}
              </span>
              <details v-else-if="segment.forwardedMessage" class="rich-card forwarded-card">
                <summary tabindex="-1">
                  <strong>{{ segment.forwardedMessage.title }}</strong>
                  <small>{{ segment.forwardedMessage.footer }}</small>
                </summary>
                <p v-for="line in segment.forwardedMessage.previewLines" :key="line">{{ line }}</p>
              </details>
              <a
                v-else-if="segment.sharedContact"
                class="rich-card"
                :href="segment.sharedContact.jumpUrl || undefined"
                target="_blank"
                tabindex="-1"
              >
                <strong>{{ segment.sharedContact.title }}</strong>
                <p>{{ segment.sharedContact.subtitle }}</p>
                <small>{{ segment.sharedContact.tag }}</small>
              </a>
              <a
                v-else-if="segment.miniApp"
                class="mini-card"
                :href="segment.miniApp.jumpUrl || undefined"
                target="_blank"
                tabindex="-1"
              >
                <img
                  v-if="segment.miniApp.previewUrl"
                  :src="segment.miniApp.previewUrl"
                  loading="lazy"
                  decoding="async"
                  alt=""
                />
                <div>
                  <small>{{ segment.miniApp.appName }}</small>
                  <strong>{{ segment.miniApp.title }}</strong>
                  <span>{{ segment.miniApp.hostName }}</span>
                </div>
              </a>
              <span v-else class="text-segment warning">{{ segment.displayText }}</span>
            </template>
          </div>

          <details v-if="message.forwardedMessages.length > 0" class="forwarded-messages">
            <summary tabindex="-1">查看 {{ message.forwardedMessages.length }} 条转发消息</summary>
            <MessageItem
              v-for="forwarded in message.forwardedMessages"
              :key="forwarded.key"
              :message="forwarded"
              compact
              @reply-click="emit('replyClick', $event)"
            />
          </details>
        </div>

        <div v-if="message.reactions.length > 0" class="reactions">
          <span v-for="reaction in message.reactions" :key="reaction.faceId" class="reaction">
            <img v-if="reaction.faceAssetPath" :src="assetUrl(reaction.faceAssetPath)" alt="" />
            <span v-else>{{ reaction.displayText }}</span>
            <small>{{ reaction.count }}</small>
          </span>
        </div>
      </div>
    </template>
  </article>
</template>
