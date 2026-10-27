	.text

	.global OverlayFunc
	.type OverlayFunc, @function
	.thumb
OverlayFunc:
	mov r0, #2
	bx lr
	.size OverlayFunc, .-OverlayFunc
