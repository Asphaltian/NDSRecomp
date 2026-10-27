	.public NitroMain
	.public SDK_AUTOLOAD_LIST
	.public SDK_AUTOLOAD_LIST_END
	.public SDK_AUTOLOAD_START
	.public SDK_STATIC_BSS_START
	.public SDK_STATIC_BSS_END

	.text

	.global _start
	.type _start, @function
	.arm
_start:
	b NitroMain
	.size _start, .-_start

	.global _start_AutoloadDoneCallback
	.type _start_AutoloadDoneCallback, @function
_start_AutoloadDoneCallback:
	bx lr
	.size _start_AutoloadDoneCallback, .-_start_AutoloadDoneCallback

	.global _start_ModuleParams
_start_ModuleParams:
	.word SDK_AUTOLOAD_LIST
	.word SDK_AUTOLOAD_LIST_END
	.word SDK_AUTOLOAD_START
	.word SDK_STATIC_BSS_START
	.word SDK_STATIC_BSS_END
	.word 0
	.word 0x04027534
	.word 0xDEC00621
	.word 0x2106C0DE
