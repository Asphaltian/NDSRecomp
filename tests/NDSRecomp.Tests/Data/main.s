	.public FastCopy
	.public OverlayFunc

	.text

	.global NitroMain
	.type NitroMain, @function
	.arm
NitroMain:
	stmdb sp!, {r3, lr}
	blx ThumbMain
	bl FastCopy
	bl Unsized
	ldr r0, _SomeData
	ldr r1, _OverlayFunc
	ldmia sp!, {r3, pc}
_SomeData: .word SomeData
_OverlayFunc: .word OverlayFunc
	.size NitroMain, .-NitroMain

	.global ThumbMain
	.type ThumbMain, @function
	.thumb
ThumbMain:
	push {lr}
	bl ThumbHelper
local_label:
	pop {pc}
	.size ThumbMain, .-ThumbMain

	.global ThumbHelper
	.type ThumbHelper, @function
ThumbHelper:
	mov r0, #0
	bx lr
	.size ThumbHelper, .-ThumbHelper

	.global Unsized
	.type Unsized, @function
	.arm
Unsized:
	bx lr

	.data

	.global SomeData
SomeData:
	.word 0x12345678
