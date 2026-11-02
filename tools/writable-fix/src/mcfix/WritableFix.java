package mcfix;

import java.io.IOException;
import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.security.ProtectionDomain;

import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;

/**
 * 修掉「Java 认为所有路径都不可写」的机器。
 *
 * <p>背景（2026-10-29 在阿水那台电脑上实测）：那台 Windows 的访问检查对**写**一律返回拒绝，
 * 连「只给 Everyone 完全控制」的目录、连 Java 自己刚建的文件都判不可写，而实际写入完全正常。
 * 17 / 21 / 25 / 8 四个 JRE 表现一致，所以不是 JDK 版本问题。
 *
 * <p>后果：Fabric 启动时要把游戏 jar 重映射一遍，它用 <c>ZipFileSystem</c> 写
 * <c>.fabric/remappedJars/…/client-intermediary.jar.tmp</c>，而 zipfs 的「可写」判断就是
 * <c>readOnly = !Files.isWritable(path)</c>（已用 javap 反编译确认）。判断一坏，zipfs 直接只读，
 * 连 create=true 新建的 zip 都拒绝写入，于是报
 * <c>the jar file … can't be written</c>，Fabric 永远起不来。
 *
 * <p>做法：把 <c>Files.isWritable(Path)</c> 的方法体换成 <c>path.toFile().canWrite()</c>。
 * 后者走的是老的 CRT 判断（只看 DOS 只读属性），在这台机器上是正确的。
 *
 * <p>只在检测到「检查已坏」时才动手：健康机器上 premain 直接返回，一个字节都不改。
 */
public final class WritableFix {

    private WritableFix() {
    }

    public static void premain(String args, Instrumentation inst) {
        // premain 抛异常会让 JVM 直接退出，所以整段都要兜住
        try {
            install(inst);
        } catch (Throwable t) {
            System.out.println("[WritableFix] 跳过（" + t + "）");
        }
    }

    private static void install(Instrumentation inst) {
        // ASM 不打进这个 jar：-javaagent 会把 agent jar 并入系统 classpath，
        // 里面再带一份 org.objectweb.asm 就会被 Fabric 的重复类检查抓住
        // （"duplicate ASM classes found on classpath"），游戏直接起不来。
        // 所以 ASM 从游戏自己的库路径拿 —— Fabric 本来就要依赖它。
        if (!looksBroken()) {
            System.out.println("[WritableFix] 可写性检查正常，跳过");
            return;
        }

        System.out.println("[WritableFix] 检测到 Files.isWritable 判定失效，打补丁");

        ClassFileTransformer transformer = new ClassFileTransformer() {
            @Override
            public byte[] transform(ClassLoader loader, String className, Class<?> beingRedefined,
                                    ProtectionDomain domain, byte[] classfileBuffer) {
                if (!"java/nio/file/Files".equals(className)) return null;
                try {
                    return patch(classfileBuffer);
                } catch (Throwable t) {
                    System.out.println("[WritableFix] 打补丁失败：" + t);
                    return null;
                }
            }
        };

        inst.addTransformer(transformer, true);
        try {
            inst.retransformClasses(Files.class);
        } catch (Throwable t) {
            System.out.println("[WritableFix] 重定义失败：" + t);
        }
    }

    /**
     * 用「工作目录 + 一个刚建的临时文件」两路判断。
     *
     * <p>原则是「证明不了正常就当坏的」：这台机器的临时目录本身也可能写不了
     * （{@code Files.createTempFile} 抛异常），那种情况不能当成「一切正常」放过去。
     */
    private static boolean looksBroken() {
        Path probe = Paths.get(System.getProperty("user.dir", "."));
        boolean dirWritable = Files.isWritable(probe);
        System.out.println("[WritableFix] 探测 user.dir=" + probe + " isWritable=" + dirWritable);
        if (dirWritable) return false;

        try {
            Path tmp = Files.createTempFile("writablefix", ".tmp");
            try {
                boolean fileWritable = Files.isWritable(tmp);
                System.out.println("[WritableFix] 探测新建文件 isWritable=" + fileWritable);
                return !fileWritable;
            } finally {
                Files.deleteIfExists(tmp);
            }
        } catch (IOException e) {
            System.out.println("[WritableFix] 临时文件探测失败（" + e + "），按失效处理");
            return true;
        }
    }

    /** 把 isWritable(Path)Z 的方法体整段换掉。 */
    private static byte[] patch(byte[] buffer) {
        ClassReader reader = new ClassReader(buffer);
        ClassWriter writer = new ClassWriter(0);

        reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
            @Override
            public MethodVisitor visitMethod(int access, String name, String descriptor,
                                             String signature, String[] exceptions) {
                MethodVisitor target = super.visitMethod(access, name, descriptor, signature, exceptions);
                if (!"isWritable".equals(name) || !"(Ljava/nio/file/Path;)Z".equals(descriptor)) {
                    return target;
                }

                // 不转发原来的指令：返回一个「吃掉所有访问」的 MethodVisitor，只写新方法体
                return new MethodVisitor(Opcodes.ASM9) {
                    @Override
                    public void visitCode() {
                        target.visitCode();
                        target.visitVarInsn(Opcodes.ALOAD, 0);
                        target.visitMethodInsn(Opcodes.INVOKEINTERFACE, "java/nio/file/Path",
                                "toFile", "()Ljava/io/File;", true);
                        target.visitMethodInsn(Opcodes.INVOKEVIRTUAL, "java/io/File",
                                "canWrite", "()Z", false);
                        target.visitInsn(Opcodes.IRETURN);
                    }

                    @Override
                    public void visitMaxs(int maxStack, int maxLocals) {
                        target.visitMaxs(1, 1);
                    }

                    @Override
                    public void visitEnd() {
                        target.visitEnd();
                    }
                };
            }
        }, 0);

        return writer.toByteArray();
    }
}
