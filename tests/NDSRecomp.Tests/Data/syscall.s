	.text

	.global SVC_SoftReset
	.thumb
SVC_SoftReset:
	swi 0
	bx lr
	.size SVC_SoftReset, .-SVC_SoftReset
