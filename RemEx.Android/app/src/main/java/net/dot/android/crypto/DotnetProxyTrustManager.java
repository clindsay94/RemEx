package net.dot.android.crypto;

import javax.net.ssl.X509TrustManager;
import java.security.cert.X509Certificate;
import java.security.cert.CertificateException;

public final class DotnetProxyTrustManager implements X509TrustManager {
    private final long nativeHandle;

    public DotnetProxyTrustManager(long nativeHandle) {
        this.nativeHandle = nativeHandle;
    }

    @Override
    public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException {
        throw new CertificateException("Client trust validation not supported");
    }

    @Override
    public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
        if (!verifyRemoteCertificate(nativeHandle)) {
            throw new CertificateException("Certificate validation failed");
        }
    }

    @Override
    public X509Certificate[] getAcceptedIssuers() {
        return new X509Certificate[0];
    }

    private native boolean verifyRemoteCertificate(long handle);
}
