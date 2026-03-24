package net.dot.android.crypto;

import java.nio.ByteBuffer;
import java.security.InvalidKeyException;
import java.security.NoSuchAlgorithmException;

import javax.crypto.Mac;
import javax.crypto.ShortBufferException;
import javax.crypto.spec.SecretKeySpec;

public final class PalPbkdf2 {
    private static final int ERROR_UNSUPPORTED_ALGORITHM = -1;
    private static final int SUCCESS = 1;

    private PalPbkdf2() {
    }

    public static int pbkdf2OneShot(String algorithmName, byte[] password, ByteBuffer salt, int iterations, ByteBuffer destination)
        throws ShortBufferException, InvalidKeyException, IllegalArgumentException {
        if (algorithmName == null || password == null || destination == null) {
            throw new IllegalArgumentException("algorithmName, password, and destination must not be null.");
        }

        String javaAlgorithmName = "Hmac" + algorithmName;
        Mac mac;

        try {
            mac = Mac.getInstance(javaAlgorithmName);
        } catch (NoSuchAlgorithmException exception) {
            return ERROR_UNSUPPORTED_ALGORITHM;
        }

        if (password.length == 0) {
            password = new byte[] { 0 };
        }

        if (salt != null) {
            salt.mark();
        }

        SecretKeySpec key = new SecretKeySpec(password, javaAlgorithmName);
        mac.init(key);

        int blockCounter = 1;
        int destinationOffset = 0;
        byte[] blockCounterBuffer = new byte[4];
        byte[] block = new byte[mac.getMacLength()];
        byte[] current = new byte[block.length];

        while (destinationOffset < destination.capacity()) {
            writeBigEndianInt(blockCounter, blockCounterBuffer);

            if (salt != null) {
                mac.update(salt);
                salt.reset();
            }

            mac.update(blockCounterBuffer);
            mac.doFinal(current, 0);
            System.arraycopy(current, 0, block, 0, block.length);

            for (int iteration = 2; iteration <= iterations; iteration++) {
                mac.update(current);
                mac.doFinal(current, 0);

                for (int index = 0; index < current.length; index++) {
                    block[index] ^= current[index];
                }
            }

            int bytesToWrite = Math.min(block.length, destination.capacity() - destinationOffset);
            destination.put(block, 0, bytesToWrite);
            destinationOffset += block.length;
            blockCounter++;
        }

        return SUCCESS;
    }

    private static void writeBigEndianInt(int value, byte[] destination) {
        destination[0] = (byte) ((value >> 24) & 0xFF);
        destination[1] = (byte) ((value >> 16) & 0xFF);
        destination[2] = (byte) ((value >> 8) & 0xFF);
        destination[3] = (byte) (value & 0xFF);
    }
}